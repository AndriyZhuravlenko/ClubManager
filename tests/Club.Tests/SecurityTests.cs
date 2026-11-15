using Club.Server.Security;

namespace Club.Tests;

public class PasswordTests
{
    [Fact]
    public void Same_password_gives_different_hashes_because_of_salt()
    {
        var a = Passwords.Hash("qwerty12");
        var b = Passwords.Hash("qwerty12");
        Assert.NotEqual(a, b);
        Assert.True(Passwords.Verify(a, "qwerty12"));
        Assert.True(Passwords.Verify(b, "qwerty12"));
    }

    [Fact]
    public void Hash_does_not_contain_password() =>
        Assert.DoesNotContain("qwerty12", Passwords.Hash("qwerty12"));

    [Fact]
    public void Wrong_password_is_rejected() =>
        Assert.False(Passwords.Verify(Passwords.Hash("qwerty12"), "qwerty13"));

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("abc", false)]
    [InlineData("    ", false)]
    [InlineData("abcd", true)]
    public void Minimal_password_rules(string? password, bool valid) =>
        Assert.Equal(valid, Passwords.Validate(password) == null);

    [Theory]
    [InlineData("  Admin ", "admin")]
    [InlineData("ІВАН", "іван")]
    [InlineData(null, "")]
    public void Logins_are_case_insensitive(string? login, string expected) =>
        Assert.Equal(expected, Passwords.NormalizeLogin(login));
}

public class LoginThrottleTests
{
    [Fact]
    public void Five_failures_lock_the_login()
    {
        var t = new LoginThrottle();
        for (var i = 0; i < 4; i++) { Assert.Null(t.Check("a")); t.Fail("a"); }
        Assert.Null(t.Check("a"));            // після 4 помилок ще можна
        t.Fail("a");
        Assert.NotNull(t.Check("a"));         // після 5-ї — заблоковано
    }

    [Fact]
    public void Lock_applies_only_to_one_login()
    {
        var t = new LoginThrottle();
        for (var i = 0; i < 5; i++) t.Fail("a");
        Assert.NotNull(t.Check("a"));
        Assert.Null(t.Check("b"));
    }

    [Fact]
    public void Successful_login_resets_the_counter()
    {
        var t = new LoginThrottle();
        for (var i = 0; i < 4; i++) t.Fail("a");
        t.Success("a");
        for (var i = 0; i < 4; i++) t.Fail("a");
        Assert.Null(t.Check("a"));
    }
}
