using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Club.Server.Security;

/// <summary>
/// TLS-сертифікат сервера. Під час першого запуску створюється самопідписаний сертифікат
/// (RSA 2048, SHA-256, термін — 5 років) і зберігається у файл; далі використовується той самий.
///
/// Агенти не покладаються на центр сертифікації: вони перевіряють, що SHA-256 відбиток
/// сертифіката сервера збігається з указаним у їхніх налаштуваннях (certificate pinning).
/// Тому підмінити сервер (атака "людина посередині") неможливо навіть із самопідписаним сертифікатом.
/// </summary>
public static class ServerCertificate
{
    public static X509Certificate2 LoadOrCreate(string path, ILogger log)
    {
        if (!File.Exists(path))
        {
            File.WriteAllBytes(path, Create().Export(X509ContentType.Pfx));
            log.LogWarning("Створено новий сертифікат сервера: {Path}", Path.GetFullPath(path));
        }

        var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password: null);
        var thumbprint = Thumbprint(certificate);

        // Відбиток зберігаємо поруч у текстовому файлі, щоб його зручно було скопіювати в налаштування агентів
        File.WriteAllText(Path.ChangeExtension(path, ".thumbprint.txt"), thumbprint);
        log.LogWarning("Відбиток сертифіката сервера (ServerCertThumbprint для агентів): {Thumbprint}", thumbprint);
        return certificate;
    }

    /// <summary>SHA-256 відбиток сертифіката — шістнадцятковий рядок без роздільників.</summary>
    public static string Thumbprint(X509Certificate certificate) =>
        certificate.GetCertHashString(HashAlgorithmName.SHA256);

    private static X509Certificate2 Create()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Club Manager Server", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        // Імена й адреси, за якими до сервера звертаються (для браузера; агенти перевіряють відбиток)
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddDnsName(Environment.MachineName);
        names.AddIpAddress(IPAddress.Loopback);
        foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()).Where(a => a.AddressFamily == AddressFamily.InterNetwork))
            names.AddIpAddress(ip);
        request.CertificateExtensions.Add(names.Build());

        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // Server Authentication

        var now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddDays(-1), now.AddYears(5));
    }
}
