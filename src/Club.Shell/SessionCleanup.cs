namespace Club.Shell;

/// <summary>
/// Очищення профілю користувача після сеансу: видаляє дані входу в месенджери й браузери,
/// завантажені файли тощо, щоб наступний клієнт не потрапив у чужі акаунти.
/// Виконується від імені самого користувача (оболонка працює в його сеансі), тому змінні
/// %APPDATA%, %LOCALAPPDATA%, %USERPROFILE% вказують саме на його профіль.
/// Список шляхів задається в налаштуваннях агента (SessionCleanup).
/// </summary>
public static class SessionCleanup
{
    private static int _running;

    public static void RunInBackground(List<string>? paths)
    {
        if (paths == null || paths.Count == 0) return;
        if (Interlocked.Exchange(ref _running, 1) == 1) return; // уже виконується

        Task.Run(async () =>
        {
            try
            {
                // Даємо агенту закрити програми, щоб файли не були зайняті
                await Task.Delay(TimeSpan.FromSeconds(3));
                foreach (var raw in paths)
                    Clean(Environment.ExpandEnvironmentVariables(raw));
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        });
    }

    private static void Clean(string path)
    {
        // Записи реєстру користувача: "HKCU\Розділ\Параметр" — видаляється параметр,
        // а якщо такого параметра немає — розділ цілком
        if (path.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase))
        {
            CleanRegistry(path[5..]);
            return;
        }

        try
        {
            // "папка\*" — видалити лише вміст папки, саму папку залишити
            if (path.EndsWith(@"\*"))
            {
                var folder = path[..^2];
                if (!Directory.Exists(folder)) return;
                foreach (var dir in Directory.GetDirectories(folder)) TryDelete(() => Directory.Delete(dir, recursive: true));
                foreach (var file in Directory.GetFiles(folder)) TryDelete(() => File.Delete(file));
                return;
            }

            if (Directory.Exists(path)) TryDelete(() => Directory.Delete(path, recursive: true));
            else if (File.Exists(path)) TryDelete(() => File.Delete(path));
        }
        catch
        {
            // Очищення не повинно ламати оболонку: зайняті чи захищені файли просто пропускаємо
        }
    }

    private static void CleanRegistry(string keyAndValue)
    {
        try
        {
            var split = keyAndValue.LastIndexOf('\\');
            if (split <= 0) return;
            var keyPath = keyAndValue[..split];
            var valueName = keyAndValue[(split + 1)..];

            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath, writable: true))
            {
                if (key?.GetValue(valueName) != null)
                {
                    key.DeleteValue(valueName, throwOnMissingValue: false);
                    return;
                }
            }
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(keyAndValue, throwOnMissingSubKey: false);
        }
        catch
        {
            // немає прав або розділ не існує — пропускаємо
        }
    }

    private static void TryDelete(Action delete)
    {
        try { delete(); } catch { /* файл зайнятий або немає прав — пропускаємо */ }
    }
}
