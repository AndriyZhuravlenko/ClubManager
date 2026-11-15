namespace Club.Agent;

/// <summary>
/// Журнал у файл. Служба Windows не має консолі, тому без файлу її повідомлення не видно.
/// Ротація: коли agent.log перевищує 5 МБ, він перейменовується на agent.1.log
/// (старіші зсуваються: agent.1 → agent.2 …), зберігається не більше 5 старих файлів.
/// Отже, журнал агента займає не більше ~30 МБ, скільки б ПК не працював.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const long MaxFileSize = 5 * 1024 * 1024;
    private const int MaxArchives = 5;

    private readonly string _path;
    private readonly object _lock = new();
    private int _writesSinceCheck;

    public FileLoggerProvider(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RotateIfNeeded(); // на випадок, якщо файл уже великий після попередньої версії агента
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(string line)
    {
        lock (_lock)
        {
            // Розмір перевіряємо не на кожен запис, а раз на 100 — це дешевше
            if (++_writesSinceCheck >= 100)
            {
                _writesSinceCheck = 0;
                RotateIfNeeded();
            }
            File.AppendAllText(_path, line + Environment.NewLine);
        }
    }

    private void RotateIfNeeded()
    {
        try
        {
            var file = new FileInfo(_path);
            if (!file.Exists || file.Length < MaxFileSize) return;

            string Archive(int n) => Path.Combine(file.DirectoryName!, $"{Path.GetFileNameWithoutExtension(_path)}.{n}{file.Extension}");

            if (File.Exists(Archive(MaxArchives))) File.Delete(Archive(MaxArchives));
            for (var n = MaxArchives - 1; n >= 1; n--)
                if (File.Exists(Archive(n))) File.Move(Archive(n), Archive(n + 1));
            File.Move(_path, Archive(1));
        }
        catch
        {
            // Якщо ротація не вдалася (файл зайнятий) — спробуємо наступного разу
        }
    }

    public void Dispose() { }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{logLevel}] {category}: {formatter(state, exception)}";
            if (exception != null) line += Environment.NewLine + exception;
            try { provider.Write(line); } catch { /* журнал не повинен ламати агента */ }
        }
    }
}
