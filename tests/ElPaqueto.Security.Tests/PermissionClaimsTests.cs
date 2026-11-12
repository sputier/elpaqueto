using System.Security.Claims;
using ElPaqueto.Security.Authentication;
using ElPaqueto.Security.Authorization;

namespace ElPaqueto.Security.Tests;

public class PermissionClaimsTests
{
    private static readonly string[] ReadMethods = ["GET", "HEAD"];

    private static ClaimsPrincipal Authenticated(params string[] permissionClaims)
    {
        var claims = permissionClaims.Select(value => new Claim(ElPaquetoClaimTypes.Permission, value));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test"));
    }

    [Fact]
    public void AnAuthenticatedPrincipal_HoldsExactlyTheFlagsOfItsClaims()
    {
        var principal = Authenticated("Read", "Publish");

        Assert.Equal(Permission.Read | Permission.Publish, principal.GrantedPermissions());
        Assert.True(principal.HasPermission(Permission.Read));
        Assert.True(principal.HasPermission(Permission.Read | Permission.Publish));
        Assert.False(principal.HasPermission(Permission.Admin));
        Assert.False(principal.HasPermission(Permission.Read | Permission.Admin));
    }

    [Fact]
    public void TheFlagsAreIndependent_AdminImpliesNothing()
    {
        var principal = Authenticated("Admin");

        Assert.Equal(Permission.Admin, principal.GrantedPermissions());
        Assert.False(principal.HasPermission(Permission.Read));
        Assert.False(principal.HasPermission(Permission.Publish));
    }

    [Fact]
    public void AnUnauthenticatedPrincipal_HoldsNothing_WhateverClaimsItCarries()
    {
        var claims = new[] { new Claim(ElPaquetoClaimTypes.Permission, "Admin") };
        var unauthenticated = new ClaimsPrincipal(new ClaimsIdentity(claims));

        Assert.False(unauthenticated.Identity!.IsAuthenticated);
        Assert.Equal(Permission.None, unauthenticated.GrantedPermissions());
        Assert.False(unauthenticated.HasPermission(Permission.Read));
        Assert.False(unauthenticated.HasPermission(Permission.None));
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("READ")]
    [InlineData("None")]
    [InlineData("7")]
    [InlineData("Read, Publish")]
    [InlineData("")]
    [InlineData("Superuser")]
    public void ClaimValuesThatAreNotExactlyADefinedFlagName_GrantNothing(string value)
    {
        Assert.Equal(Permission.None, Authenticated(value).GrantedPermissions());
    }

    [Fact]
    public void AuthenticatedPrincipalWithoutClaims_HoldsNoneButSatisfiesNone()
    {
        var principal = Authenticated();

        Assert.Equal(Permission.None, principal.GrantedPermissions());
        Assert.True(principal.HasPermission(Permission.None));
        Assert.False(principal.HasPermission(Permission.Read));
    }

    [Fact]
    public void TheClaimTypeIsTheOneTheHandlerEmits()
    {
        Assert.Equal("urn:elpaqueto:permission", ElPaquetoClaimTypes.Permission);
    }

    [Fact]
    public void EveryRowOfThePermissionTable_IsConsistent()
    {
        var rules = EndpointPermissionTable.Rules;

        Assert.Equal(rules.Count, rules.Select(rule => rule.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(rules, rule => Assert.NotEmpty(rule.Methods));
        Assert.All(rules, rule => Assert.All(rule.Methods, method => Assert.Equal(method.ToUpperInvariant(), method)));
        Assert.All(rules.Where(rule => !rule.Anonymous), rule => Assert.NotEqual(Permission.None, rule.Required));
    }

    [Fact]
    public void OnlyTheLivenessEndpoint_IsAnonymous()
    {
        var anonymous = EndpointPermissionTable.Rules.Where(rule => rule.Anonymous).Select(rule => rule.Key);

        Assert.Equal([EndpointPermissionTable.HealthKey], anonymous);
    }

    [Fact]
    public void NoReadRoute_AllowsAMethodThatChangesState()
    {
        var reads = EndpointPermissionTable.Rules.Where(rule => rule.Required == Permission.Read);

        Assert.All(reads, rule => Assert.All(rule.Methods, method => Assert.Contains(method, ReadMethods)));
    }

    [Fact]
    public void WriteRows_RequirePublish_AndNothingElseDoes()
    {
        var publishers = EndpointPermissionTable.Rules
            .Where(rule => rule.Required.HasFlag(Permission.Publish))
            .Select(rule => rule.Key)
            .OrderBy(key => key, StringComparer.Ordinal);

        Assert.Equal(["delete", "relist", "upload-package", "upload-symbol"], publishers);
    }
}
