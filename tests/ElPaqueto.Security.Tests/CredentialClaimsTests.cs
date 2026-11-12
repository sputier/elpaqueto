using System.Security.Claims;
using ElPaqueto.Security.ApiKeys;
using ElPaqueto.Security.Authentication;
using Microsoft.AspNetCore.Http;

namespace ElPaqueto.Security.Tests;

/// <summary>
/// Which credential authenticated a request, as read by the write rule of the permission table and by the
/// replacement of the inherited inline API key check.
/// </summary>
public class CredentialClaimsTests
{
    private const string KeyId = "AbCdEfGh1234";
    private const string Token = "epq_" + KeyId + "_0123456789012345678901234567890123456789abc";
    private const string OtherToken = "epq_ZZZZZZZZZZZZ_0123456789012345678901234567890123456789abc";

    private static ClaimsIdentity Identity(string? method, string? source, string? keyId, params string[] permissions)
    {
        var claims = new List<Claim>();
        if (method is not null)
        {
            claims.Add(new Claim(ElPaquetoClaimTypes.AuthenticationMethod, method));
        }

        if (source is not null)
        {
            claims.Add(new Claim(ElPaquetoClaimTypes.CredentialSource, source));
        }

        if (keyId is not null)
        {
            claims.Add(new Claim(ElPaquetoClaimTypes.ApiKeyId, keyId));
        }

        claims.AddRange(permissions.Select(p => new Claim(ElPaquetoClaimTypes.Permission, p)));
        return new ClaimsIdentity(claims, authenticationType: "test");
    }

    private static ClaimsPrincipal HeaderKey(params string[] permissions)
        => new(Identity(ElPaquetoClaimTypes.ApiKeyMethod, ElPaquetoClaimTypes.ApiKeyHeaderSource, KeyId, permissions));

    [Fact]
    public void AKeyFromTheHeader_IsRecognized()
    {
        Assert.Equal(KeyId, HeaderKey("Publish").ApiKeyIdFromHeader());
    }

    [Fact]
    public void AKeySentAsBasicPassword_APasswordOrNothing_IsNotAHeaderKey()
    {
        var basicKey = new ClaimsPrincipal(Identity(ElPaquetoClaimTypes.ApiKeyMethod, ElPaquetoClaimTypes.BasicPasswordSource, KeyId, "Publish"));
        var password = new ClaimsPrincipal(Identity(ElPaquetoClaimTypes.PasswordMethod, null, null, "Publish"));
        var passwordClaimingHeader = new ClaimsPrincipal(Identity(ElPaquetoClaimTypes.PasswordMethod, ElPaquetoClaimTypes.ApiKeyHeaderSource, KeyId, "Publish"));
        var noKeyId = new ClaimsPrincipal(Identity(ElPaquetoClaimTypes.ApiKeyMethod, ElPaquetoClaimTypes.ApiKeyHeaderSource, null, "Publish"));

        Assert.Null(basicKey.ApiKeyIdFromHeader());
        Assert.Null(password.ApiKeyIdFromHeader());
        Assert.Null(passwordClaimingHeader.ApiKeyIdFromHeader());
        Assert.Null(noKeyId.ApiKeyIdFromHeader());
        Assert.Null(new ClaimsPrincipal().ApiKeyIdFromHeader());
    }

    [Fact]
    public void AnUnauthenticatedIdentity_IsNeverAHeaderKey()
    {
        var claims = Identity(ElPaquetoClaimTypes.ApiKeyMethod, ElPaquetoClaimTypes.ApiKeyHeaderSource, KeyId, "Publish").Claims;

        Assert.Null(new ClaimsPrincipal(new ClaimsIdentity(claims)).ApiKeyIdFromHeader());
    }

    [Fact]
    public void AmbiguousClaims_AreNeverTrusted()
    {
        var twoIdentities = HeaderKey("Publish");
        twoIdentities.AddIdentity(Identity(ElPaquetoClaimTypes.PasswordMethod, null, null, "Admin"));
        var twoKeyIds = HeaderKey("Publish");
        ((ClaimsIdentity)twoKeyIds.Identity!).AddClaim(new Claim(ElPaquetoClaimTypes.ApiKeyId, "ZZZZZZZZZZZZ"));
        var twoSources = HeaderKey("Publish");
        ((ClaimsIdentity)twoSources.Identity!).AddClaim(new Claim(ElPaquetoClaimTypes.CredentialSource, ElPaquetoClaimTypes.BasicPasswordSource));

        Assert.Null(twoIdentities.ApiKeyIdFromHeader());
        Assert.Null(twoKeyIds.ApiKeyIdFromHeader());
        Assert.Null(twoSources.ApiKeyIdFromHeader());
    }

    [Fact]
    public async Task TheInlineCheck_AcceptsOnlyTheHeaderKeyOfThePrincipalWithPublish()
    {
        Assert.True(await InlineCheck(HeaderKey("Publish"), Token));
        Assert.True(await InlineCheck(HeaderKey("Read", "Publish", "Admin"), Token));

        Assert.False(await InlineCheck(HeaderKey("Publish"), OtherToken));
        Assert.False(await InlineCheck(HeaderKey("Publish"), null));
        Assert.False(await InlineCheck(HeaderKey("Publish"), string.Empty));
        Assert.False(await InlineCheck(HeaderKey("Publish"), " " + Token));
        Assert.False(await InlineCheck(HeaderKey("Publish"), "legacy-plain-text-key"));
        Assert.False(await InlineCheck(HeaderKey("Read", "Admin"), Token));
        Assert.False(await InlineCheck(
            new ClaimsPrincipal(Identity(ElPaquetoClaimTypes.ApiKeyMethod, ElPaquetoClaimTypes.BasicPasswordSource, KeyId, "Publish")),
            Token));
        Assert.False(await InlineCheck(new ClaimsPrincipal(Identity(ElPaquetoClaimTypes.PasswordMethod, null, null, "Publish")), Token));
        Assert.False(await InlineCheck(new ClaimsPrincipal(new ClaimsIdentity()), Token));
    }

    [Fact]
    public async Task TheInlineCheck_RefusesWhenThereIsNoRequest()
    {
        var service = new ApiKeyPrincipalAuthenticationService(new HttpContextAccessor());

        Assert.False(await service.AuthenticateAsync(Token, CancellationToken.None));
    }

    private static Task<bool> InlineCheck(ClaimsPrincipal user, string? presented)
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = user } };
        return new ApiKeyPrincipalAuthenticationService(accessor).AuthenticateAsync(presented!, CancellationToken.None);
    }
}
