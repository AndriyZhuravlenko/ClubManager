namespace Club.Agent;

/// <summary>Налаштування агента з appsettings.json (розділ "Agent").</summary>
public class AgentOptions
{
    public string ServerUrl { get; set; } = "https://192.168.56.1:5001";
    public string AgentKey { get; set; } = "";

    /// <summary>
    /// SHA-256 відбиток сертифіката сервера (з файлу server-cert.thumbprint.txt поруч із сервером).
    /// Агент підключається лише до сервера з саме цим сертифікатом.
    /// </summary>
    public string ServerCertThumbprint { get; set; } = "";

    /// <summary>Папки, програми з яких дозволені завжди (драйвери, антивірус, компоненти Windows).</summary>
    public string[] AlwaysAllowedFolders { get; set; } = [];

    /// <summary>Закривати всі програми користувача після завершення сеансу.</summary>
    public bool CloseAppsOnSessionEnd { get; set; } = true;

    /// <summary>
    /// Що видаляти з профілю користувача після сеансу (дані входу в месенджери й браузери,
    /// завантаження). Змінні середовища (%APPDATA% тощо) — користувача; "\*" у кінці — лише вміст папки.
    /// </summary>
    public string[] SessionCleanup { get; set; } = [];

    /// <summary>
    /// Що видаляти після сеансу на рівні всього ПК (від імені SYSTEM) — дані входу програм,
    /// які зберігають їх у власній папці встановлення (напр., Steam). Можна вказати шаблон
    /// імені файлу в останній частині шляху (ssfn*).
    /// </summary>
    public string[] MachineCleanup { get; set; } = [];
}
