using Club.Agent;
using Club.Server.Security;

namespace Club.Tests;

/// <summary>Правила контролю програм на клієнтському ПК (агент).</summary>
public class BlockRuleTests
{
    [Theory]
    [InlineData("taskmgr.exe", @"C:\Windows\System32\Taskmgr.exe", true)]    // регістр не важливий
    [InlineData("taskmgr.exe", @"C:\Windows\System32\taskmgr2.exe", false)]
    [InlineData("cheat*.exe", @"D:\Tools\CheatEngine.exe", true)]            // зірочка — будь-які символи
    [InlineData("cheat*.exe", @"D:\Tools\mycheat.exe", false)]               // але лише з початку імені
    [InlineData("game?.exe", @"C:\G\game1.exe", true)]                       // знак питання — один символ
    [InlineData("game?.exe", @"C:\G\game12.exe", false)]
    public void Rule_by_file_name(string pattern, string path, bool expected) =>
        Assert.Equal(expected, BlockRule.Create(pattern).Matches(path, Path.GetFileName(path)));

    [Theory]
    [InlineData(@"C:\Games\Bad", @"C:\Games\Bad\run.exe", true)]
    [InlineData(@"C:\Games\Bad\", @"C:\Games\Bad\sub\run.exe", true)]
    [InlineData(@"C:\Games\Bad", @"C:\Games\BadLuck\run.exe", false)]       // сусідня папка не зачіпається
    [InlineData(@"C:\Tools\hack.exe", @"C:\Tools\hack.exe", true)]          // правило на конкретний файл
    [InlineData(@"C:\Tools\hack.exe", @"C:\Tools\hack.exe.bak", false)]
    [InlineData(@"c:\tools\HACK.exe", @"C:\Tools\hack.exe", true)]
    public void Rule_by_path(string pattern, string path, bool expected) =>
        Assert.Equal(expected, BlockRule.Create(pattern).Matches(path, Path.GetFileName(path)));
}

public class WhitelistTests
{
    private static readonly HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase) { @"C:\Games\CS2\cs2.exe" };
    private static readonly string[] Folders = [AppControl.NormalizeFolder(@"C:\Games\CS2"), AppControl.NormalizeFolder(@"C:\Windows")];

    [Theory]
    [InlineData(@"C:\Games\CS2\cs2.exe", true)]                 // файл із каталогу
    [InlineData(@"c:\games\cs2\bin\launcher.exe", true)]        // інший файл у папці програми з каталогу
    [InlineData(@"C:\Windows\notepad.exe", true)]               // системна папка дозволена завжди
    [InlineData(@"C:\Games\CS2X\cs2.exe", false)]               // схожа, але інша папка
    [InlineData(@"C:\Users\guest\Downloads\game.exe", false)]
    public void Allowed_programs(string path, bool expected) =>
        Assert.Equal(expected, AppControl.IsAllowed(path, Files, Folders));

    [Fact]
    public void Folder_is_normalized_with_trailing_backslash() =>
        Assert.Equal(@"C:\Games\CS2\", AppControl.NormalizeFolder(@"C:\Games\CS2\\"));
}

/// <summary>
/// Вхід власника без сервера: агент перевіряє хеш, створений сервером.
/// Тест гарантує, що формати хешів сервера та агента сумісні.
/// </summary>
public class OfflineOwnerHashTests
{
    private static readonly string ServerHash = Passwords.Hash("Secret-123");

    [Fact]
    public void Agent_accepts_correct_password_hashed_by_server() =>
        Assert.True(OwnerCache.VerifyHash(ServerHash, "Secret-123"));

    [Fact]
    public void Agent_rejects_wrong_password() =>
        Assert.False(OwnerCache.VerifyHash(ServerHash, "secret-123"));

    [Theory]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("AQAAAA==")]          // обрізаний хеш
    public void Agent_rejects_damaged_hash(string hash) =>
        Assert.False(OwnerCache.VerifyHash(hash, "Secret-123"));
}
