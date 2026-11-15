using Club.Agent;

// Агент можна запускати двома способами:
//  1) як звичайну консольну програму — зручно для перевірки;
//  2) як службу Windows (див. scripts/install-agent.cmd) — так він працює "по-справжньому".
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(o => o.ServiceName = "ClubAgent");

// Журнал агента у файл: C:\ClubAgent\logs\agent.log
builder.Logging.AddProvider(new FileLoggerProvider(
    Path.Combine(AppContext.BaseDirectory, "logs", "agent.log")));
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));
builder.Services.AddSingleton<ShellPipeServer>();
builder.Services.AddSingleton<OwnerCache>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ShellPipeServer>());
builder.Services.AddSingleton<AgentWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentWorker>());
builder.Services.AddHostedService<ShellLoginServer>();
builder.Services.AddHostedService<AppControl>();
builder.Services.AddHostedService<ShellWatchdog>();

var host = builder.Build();

// Якщо агент впаде через непередбачену помилку — причина залишиться в журналі
var crashLog = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Club.Agent.Crash");
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    crashLog.LogCritical(e.ExceptionObject as Exception, "Агент аварійно завершився");
TaskScheduler.UnobservedTaskException += (_, e) =>
    crashLog.LogError(e.Exception, "Необроблена помилка у фоновій задачі");

host.Run();
