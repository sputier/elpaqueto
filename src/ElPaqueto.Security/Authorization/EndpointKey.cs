using Microsoft.AspNetCore.Http;
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

        var page = endpoint.Metadata.GetMetadata<PageActionDescriptor>()?.ViewEnginePath;
        return string.IsNullOrEmpty(page) ? null : "page:" + page;
    }
}
