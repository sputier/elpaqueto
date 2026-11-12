using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;

namespace ElPaqueto.Security.Authorization;

/// <summary>
/// Finds the key under which an endpoint appears in <see cref="EndpointPermissionTable"/>.
/// </summary>
internal static class EndpointKey
{
    /// <summary>
    /// Returns the key of an endpoint, or <see langword="null"/> when it has none (which means: denied).
    /// </summary>
    /// <remarks>
    /// Controller endpoints carry their route name; endpoints mapped by name (health) carry an endpoint
    /// name; Razor pages carry neither and are identified by their page path.
    /// </remarks>
    public static string? Resolve(Endpoint? endpoint)
    {
        if (endpoint is null)
        {
            return null;
        }

        var routeName = endpoint.Metadata.GetMetadata<IRouteNameMetadata>()?.RouteName;
        if (!string.IsNullOrEmpty(routeName))
        {
            return routeName;
        }

        var endpointName = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;
        if (!string.IsNullOrEmpty(endpointName))
        {
            return endpointName;
        }

        var page = endpoint.Metadata.GetMetadata<PageActionDescriptor>();
        if (page is null || string.IsNullOrEmpty(page.ViewEnginePath))
        {
            return null;
        }

        // The view engine path does not contain the area: without it a page of an area would take the row
        // of the root page that has the same name.
        return string.IsNullOrEmpty(page.AreaName)
            ? "page:" + page.ViewEnginePath
            : "page:/" + page.AreaName + page.ViewEnginePath;
    }

    /// <summary>
    /// Returns <c>Controller.Action</c> for a controller endpoint, or <see langword="null"/> for anything else.
    /// </summary>
    public static string? ActionOf(Endpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var descriptor = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>();
        return descriptor is null ? null : descriptor.ControllerName + "." + descriptor.ActionName;
    }
}
