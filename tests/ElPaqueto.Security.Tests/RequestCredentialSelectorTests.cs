using System.Text;
using ElPaqueto.Security.ApiKeys;
using ElPaqueto.Security.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace ElPaqueto.Security.Tests;

/// <summary>The order in which the credential of a request is chosen (ADR 0002, decision 6).</summary>
public class RequestCredentialSelectorTests
{
    // Built at run time from a low-entropy filler: a token-shaped literal would trip the secret scanner.
    private static readonly string Token = $"epq_{new string('K', ApiKeyToken.KeyIdLength)}_{new string('a', ApiKeyToken.SecretLength)}";

    private static string Basic(string userName, string password)
        => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{userName}:{password}"));

    private static RequestCredential Select(StringValues apiKey = default, string? authorization = null)
    {
        IHeaderDictionary headers = new HeaderDictionary();
        if (apiKey.Count > 0)
        {
            headers[RequestCredentialSelector.ApiKeyHeaderName] = apiKey;
        }

        if (authorization is not null)
        {
            headers.Authorization = authorization;
        }

        return RequestCredentialSelector.Select(headers);
    }

    [Fact]
    public void NoCredential_IsNone()
    {
        Assert.Equal(CredentialKind.None, Select().Kind);
        Assert.Equal(CredentialKind.None, Select(authorization: "Bearer abc").Kind);
    }

    [Fact]
    public void TheHeaderKey_WinsOverBasicCredentials()
    {
        var credential = Select(Token, Basic("someone", "a valid password"));

        Assert.Equal(CredentialKind.ApiKey, credential.Kind);
        Assert.Equal(ElPaquetoClaimTypes.ApiKeyHeaderSource, credential.Source);
        Assert.Equal(Token, credential.Secret);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a key")]
    public void AnyHeaderValue_IsTheKeyToValidate_EvenAnInvalidOne(string value)
    {
        var credential = Select(value, Basic("someone", "a valid password"));

        Assert.Equal(CredentialKind.ApiKey, credential.Kind);
        Assert.Equal(value, credential.Secret);
    }

    [Fact]
    public void TwoHeaderValues_AreInvalid()
    {
        var credential = Select(new StringValues([Token, Token]), Basic("someone", "a valid password"));

        Assert.Equal(CredentialKind.Invalid, credential.Kind);
        Assert.Null(credential.Secret);
    }

    [Fact]
    public void ATokenShapedBasicPassword_IsAKeyOnly()
    {
        var credential = Select(authorization: Basic("apikey", Token));

        Assert.Equal(CredentialKind.ApiKey, credential.Kind);
        Assert.Equal(ElPaquetoClaimTypes.BasicPasswordSource, credential.Source);
        Assert.Null(credential.UserName);
    }

    [Theory]
    [InlineData("a valid password")]
    [InlineData("epq_almost")]
    public void AnyOtherBasicPassword_IsAPasswordOnly(string password)
    {
        var credential = Select(authorization: Basic("someone", password));

        Assert.Equal(CredentialKind.Password, credential.Kind);
        Assert.Equal("someone", credential.UserName);
        Assert.Equal(password, credential.Secret);
    }

    [Fact]
    public void AMalformedBasicHeader_IsInvalid()
    {
        Assert.Equal(CredentialKind.Invalid, Select(authorization: "Basic !!!").Kind);
        Assert.Equal(CredentialKind.Invalid, Select(authorization: "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + Token))).Kind);
    }

    [Fact]
    public void ToString_NeverShowsTheSecret()
    {
        Assert.DoesNotContain(Token, Select(Token).ToString(), StringComparison.Ordinal);
    }
}
