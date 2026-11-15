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
}
