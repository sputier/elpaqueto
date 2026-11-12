using System.Text;
using ElPaqueto.Security.Authentication;
using Microsoft.Extensions.Primitives;

namespace ElPaqueto.Security.Tests;

public class BasicCredentialsParserTests
{
    private static string Encode(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void NoHeader_IsNoResult()
    {
        Assert.Equal(BasicParseOutcome.NoResult, BasicCredentialsParser.Parse(StringValues.Empty).Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Bearer abcdef")]
    [InlineData("Digest username=\"alice\"")]
    [InlineData("NugetBasicAuthentication YWxpY2U6c2VjcmV0")]
    [InlineData("Basicfoo YWxpY2U6c2VjcmV0")]
    [InlineData("Basic\tYWxpY2U6c2VjcmV0")]
    public void AnotherSchemeOrAnEmptyValue_IsNoResult(string header)
    {
        Assert.Equal(BasicParseOutcome.NoResult, BasicCredentialsParser.Parse(header).Outcome);
    }

    [Theory]
    [InlineData("Basic YWxpY2U6c2VjcmV0", "alice", "secret")]
    [InlineData("basic YWxpY2U6c2VjcmV0", "alice", "secret")]
    [InlineData("BASIC YWxpY2U6c2VjcmV0", "alice", "secret")]
    [InlineData("BaSiC YWxpY2U6c2VjcmV0", "alice", "secret")]
    [InlineData("Basic    YWxpY2U6c2VjcmV0", "alice", "secret")]
    public void WellFormedBasicHeader_IsParsed_WhateverTheCaseOfTheSchemeAndTheSpacing(string header, string userName, string password)
    {
        var result = BasicCredentialsParser.Parse(header);

        Assert.Equal(BasicParseOutcome.Success, result.Outcome);
        Assert.Equal(userName, result.UserName);
        Assert.Equal(password, result.Password);
    }

    [Fact]
    public void ThePasswordMayContainColons_BecauseTheSplitIsOnTheFirstOne()
    {
        var result = BasicCredentialsParser.Parse($"Basic {Encode("alice:pa:ss:word")}");

        Assert.Equal(BasicParseOutcome.Success, result.Outcome);
        Assert.Equal("alice", result.UserName);
        Assert.Equal("pa:ss:word", result.Password);
    }

    [Fact]
    public void AnEmptyPassword_IsParsed_AndLeftToTheVerification()
    {
        var result = BasicCredentialsParser.Parse($"Basic {Encode("alice:")}");

        Assert.Equal(BasicParseOutcome.Success, result.Outcome);
        Assert.Equal(string.Empty, result.Password);
    }

    [Fact]
    public void UnicodeUserNamesAndPasswords_AreDecodedAsUtf8()
    {
        var result = BasicCredentialsParser.Parse($"Basic {Encode("héloïse:mot-de-passe-é-中文-\U0001F511")}");

        Assert.Equal(BasicParseOutcome.Success, result.Outcome);
        Assert.Equal("héloïse", result.UserName);
        Assert.Equal("mot-de-passe-é-中文-\U0001F511", result.Password);
    }

    [Theory]
    [InlineData("Basic")]
    [InlineData("Basic ")]
    [InlineData("Basic     ")]
    [InlineData("Basic !!!not-base64!!!")]
    [InlineData("Basic YWxpY2U6c2VjcmV0=!")]
    [InlineData("Basic YWxpY2U6c2VjcmV")] // truncated: invalid length
    [InlineData("Basic YWxpY2U6c2VjcmV0 ")] // trailing space
    [InlineData("Basic YWxpY2U6 c2VjcmV0")] // space inside the token
    [InlineData("Basic YWxpY2U6c2VjcmV0 extra")]
    public void MalformedBasicHeaders_AreRejected(string header)
    {
        Assert.Equal(BasicParseOutcome.Malformed, BasicCredentialsParser.Parse(header).Outcome);
    }

    [Fact]
    public void BytesThatAreNotValidUtf8_AreRejected()
    {
        // 0xFF 0xFE can never appear in UTF-8.
        var bytes = new byte[] { 0x61, 0xFF, 0xFE, (byte)':', 0x62 };

        var result = BasicCredentialsParser.Parse($"Basic {Convert.ToBase64String(bytes)}");

        Assert.Equal(BasicParseOutcome.Malformed, result.Outcome);
    }

    [Theory]
    [InlineData("alice")]
    [InlineData(":secret")]
    [InlineData("")]
    public void MissingColonOrEmptyUserName_IsRejected(string decoded)
    {
        Assert.Equal(BasicParseOutcome.Malformed, BasicCredentialsParser.Parse($"Basic {Encode(decoded)}").Outcome);
    }

    [Fact]
    public void SeveralAuthorizationValues_AreRejected()
    {
        var values = new StringValues(["Basic YWxpY2U6c2VjcmV0", "Basic Ym9iOmJvYg=="]);

        Assert.Equal(BasicParseOutcome.Malformed, BasicCredentialsParser.Parse(values).Outcome);
    }

    [Fact]
    public void AHeaderOverTheSizeCap_IsRejected()
    {
        var header = "Basic " + new string('A', BasicCredentialsParser.MaxHeaderLength);

        Assert.Equal(BasicParseOutcome.Malformed, BasicCredentialsParser.Parse(header).Outcome);
    }

    [Fact]
    public void AHeaderJustUnderTheSizeCap_DoesNotThrow()
    {
        var header = "Basic " + new string('A', BasicCredentialsParser.MaxHeaderLength - "Basic ".Length);

        var outcome = BasicCredentialsParser.Parse(header).Outcome;

        // 'AAAA...' decodes to NUL bytes without a colon: well-formed base64, but not credentials.
        Assert.Equal(BasicParseOutcome.Malformed, outcome);
    }

    [Theory]
    [InlineData("alice", true)]
    [InlineData("Alice.Smith-01_x", true)]
    [InlineData("a", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("has:colon", false)]
    [InlineData("héloïse", false)]
    [InlineData("tab\tname", false)]
    public void UserNamesFollowTheAsciiAllowList(string userName, bool expected)
    {
        Assert.Equal(expected, UserNames.IsValid(userName));
    }

    [Fact]
    public void UserNamesAreLimitedTo64Characters()
    {
        Assert.True(UserNames.IsValid(new string('a', 64)));
        Assert.False(UserNames.IsValid(new string('a', 65)));
        Assert.False(UserNames.IsValid(null));
    }
}
