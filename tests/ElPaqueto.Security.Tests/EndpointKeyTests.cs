using System.Security.Claims;
using ElPaqueto.Core;
using ElPaqueto.Security.Authentication;
using ElPaqueto.Security.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;

namespace ElPaqueto.Security.Tests;

public class EndpointKeyTests
{
    private static Endpoint EndpointWith(params object[] metadata)
        => new(context => Task.CompletedTask, new EndpointMetadataCollection(metadata), "test endpoint");

    [Fact]
    public void ARouteName_IsTheKeyOfAControllerEndpoint()
    {
        var endpoint = EndpointWith(new RouteNameMetadata("search"));

        Assert.Equal("search", EndpointKey.Resolve(endpoint));
    }

    [Fact]
    public void ARootPage_UsesItsViewEnginePath()
    {
        var endpoint = EndpointWith(new PageActionDescriptor { ViewEnginePath = "/Index" });

        Assert.Equal("page:/Index", EndpointKey.Resolve(endpoint));
    }

    [Fact]
    public void APageOfAnArea_NeverTakesTheKeyOfARootPageWithTheSameName()
    {
        var root = EndpointWith(new PageActionDescriptor { ViewEnginePath = "/Index" });
        var area = EndpointWith(new PageActionDescriptor { ViewEnginePath = "/Index", AreaName = "Admin" });

        Assert.Equal("page:/Admin/Index", EndpointKey.Resolve(area));
        Assert.NotEqual(EndpointKey.Resolve(root), EndpointKey.Resolve(area));
        Assert.False(EndpointPermissionTable.TryGetRule(EndpointKey.Resolve(area)!, out _));
    }

    [Fact]
    public void AnEndpointWithoutAnyKey_HasNoRow()
    {
        Assert.Null(EndpointKey.Resolve(EndpointWith()));
        Assert.Null(EndpointKey.Resolve(null));
    }

    [Fact]
    public void ARowIsOnlyMappedForTheActionItIsMeantFor()
    {
        var intended = EndpointWith(
            new RouteNameMetadata("search"),
            new ControllerActionDescriptor { ControllerName = "Search", ActionName = "Search" });
        var impostor = EndpointWith(
            new RouteNameMetadata("search"),
            new ControllerActionDescriptor { ControllerName = "Admin", ActionName = "ResetEverything" });
        var impostorWithoutAction = EndpointWith(new RouteNameMetadata("search"));

        Assert.True(EndpointInventory.IsMapped(intended));
        Assert.False(EndpointInventory.IsMapped(impostor));
        Assert.False(EndpointInventory.IsMapped(impostorWithoutAction));
        Assert.Equal(["zz"], EndpointInventory.FindUnmapped([EndpointWith(new RouteNameMetadata("search")).WithName("zz")]));
    }

    [Fact]
    public void PermissionClaims_AreReadFromAuthenticatedIdentitiesOnly()
    {
        var authenticated = new ClaimsIdentity([new Claim(ElPaquetoClaimTypes.Permission, "Read")], authenticationType: "test");
        var anonymousIdentity = new ClaimsIdentity([new Claim(ElPaquetoClaimTypes.Permission, "Admin")]);
        var principal = new ClaimsPrincipal([authenticated, anonymousIdentity]);

        Assert.Equal(Permission.Read, principal.GrantedPermissions());
        Assert.False(principal.HasPermission(Permission.Admin));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("")]
    public void AHealthPathThatCannotLeaveRoomForTheDetailsEndpoint_IsRejected(string path)
    {
        var app = WebApplication.CreateBuilder().Build();
        var options = new ElPaquetoOptions
        {
            HealthCheck = new HealthCheckOptions { Path = path },
        };

        Assert.Throws<InvalidOperationException>(() => app.MapElPaquetoHealthChecks(options));
    }
}

internal static class EndpointExtensions
{
    public static Endpoint WithName(this Endpoint endpoint, string displayName)
        => new(endpoint.RequestDelegate!, endpoint.Metadata, displayName);
}
