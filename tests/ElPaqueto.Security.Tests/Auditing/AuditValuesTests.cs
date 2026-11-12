using System.Net;
using ElPaqueto.Security.ApiKeys;
using ElPaqueto.Security.Auditing;

namespace ElPaqueto.Security.Tests.Auditing;

public sealed class AuditValuesTests
{
    [Theory]
    [InlineData("alice")]
    [InlineData("ci-bot_2.prod")]
    [InlineData("A")]
    public void AValidUserName_IsLoggedAsIs(string userName)
    {
        Assert.Equal(userName, AuditValues.UserName(userName));
    }

    [Theory]
    [InlineData("correct horse battery staple")] // a password typed in the user name field
    [InlineData("p@ssw0rd!")]
    [InlineData("alice\r\n2026-01-01 info: forged entry")]
    [InlineData("alice\nbob")]
    [InlineData("alice\u0000")]
    [InlineData("alïce")]
    [InlineData("ali:ce")]
    public void AnInvalidUserName_IsLoggedAsAFingerprintOnly(string userName)
    {
        var logged = AuditValues.UserName(userName);

        Assert.Matches("^<invalid:[0-9a-f]{8}>$", logged);
        Assert.DoesNotContain("alice", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", logged, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", logged, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("hunter2")]
    [InlineData("Sentinel-Pw-0123456789")]
    [InlineData("epq_0123456789ab_0123456789012345678901234567890123456789012")]
    [InlineData("alice\r\nforged")]
    public void AUserNameThatMatchesNoAccount_IsAlwaysAFingerprint(string userName)
    {
        var logged = AuditValues.UnknownUserName(userName);

        Assert.Matches("^<unknown:[0-9a-f]{8}>$", logged);
        Assert.Equal(logged, AuditValues.UnknownUserName(userName));
    }

    [Fact]
    public void ATokenShapedName_IsNeverLoggedAsIs()
    {
        Assert.Matches("^<invalid:[0-9a-f]{8}>$", AuditValues.UserName("epq_0123456789ab_0123456789012345678901234567890123456789012"));
        Assert.Matches("^<invalid:[0-9a-f]{8}>$", AuditValues.UserName("EPQ_x"));
    }

    [Fact]
    public void ATooLongUserName_IsLoggedAsAFingerprint()
    {
        Assert.Equal("a" + new string('b', 63), AuditValues.UserName("a" + new string('b', 63)));
        Assert.Matches("^<invalid:[0-9a-f]{8}>$", AuditValues.UserName(new string('b', 65)));
    }

    [Fact]
    public void TheFingerprint_CorrelatesEqualValuesAndSeparatesDifferentOnes()
    {
        var first = AuditValues.UserName("same value!");
        var again = AuditValues.UserName("same value!");
        var other = AuditValues.UserName("other value!");

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AMissingUserName_IsLoggedAsADash(string? userName)
    {
        Assert.Equal("-", AuditValues.UserName(userName));
        Assert.Equal("-", AuditValues.UnknownUserName(userName));
    }

    [Fact]
    public void ClientAddresses_AreLoggedInTheirUsualForm()
    {
        Assert.Equal("-", AuditValues.Client(null));
        Assert.Equal("203.0.113.10", AuditValues.Client(IPAddress.Parse("203.0.113.10")));
        Assert.Equal("203.0.113.10", AuditValues.Client(IPAddress.Parse("::ffff:203.0.113.10")));
        Assert.Equal("2001:db8::1", AuditValues.Client(IPAddress.Parse("2001:db8::1")));
    }

    [Theory]
    [InlineData("get", "GET")]
    [InlineData("PUT", "PUT")]
    [InlineData("DELETE", "DELETE")]
    [InlineData("PROPFIND", "OTHER")]
    [InlineData("GET\r\nX: y", "OTHER")]
    [InlineData(null, "OTHER")]
    public void RequestMethods_AreLoggedFromAFixedList(string? method, string expected)
    {
        Assert.Equal(expected, AuditValues.HttpMethod(method));
    }

    [Theory]
    [InlineData("http", "http")]
    [InlineData("HTTP", "http")]
    [InlineData("ftp", "other")]
    [InlineData(null, "other")]
    public void Schemes_AreLoggedFromAFixedList(string? scheme, string expected)
    {
        Assert.Equal(expected, AuditValues.Scheme(scheme));
    }

    [Fact]
    public void EveryApiKeyFailure_HasItsOwnReason()
    {
        var failures = Enum.GetValues<ApiKeyValidationFailure>().Where(f => f != ApiKeyValidationFailure.None).ToList();

        var reasons = failures.Select(AuditValues.Reason).ToList();

        Assert.DoesNotContain("other", reasons);
        Assert.Equal(reasons.Count, reasons.Distinct().Count());
        Assert.All(reasons, r => Assert.Matches("^[a-z_]+$", r));
    }
}
