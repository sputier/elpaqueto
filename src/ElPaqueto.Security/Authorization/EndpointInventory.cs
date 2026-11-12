using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace ElPaqueto.Security.Authorization;

/// <summary>
/// Compares the endpoints that were really built with <see cref="EndpointPermissionTable"/>.
/// </summary>
internal static class EndpointInventory
{
    /// <summary>
    /// Returns the endpoints that can match a request but have no row in the table: they are denied.
    /// </summary>
    /// <param name="endpoints">Every endpoint of the application.</param>
    /// <returns>The display name of each unmapped endpoint.</returns>
    public static IReadOnlyList<string> FindUnmapped(IEnumerable<Endpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return endpoints
            .Where(IsMatchable)
            .Where(endpoint =>
            {
                var key = EndpointKey.Resolve(endpoint);
                return key is null || !EndpointPermissionTable.TryGetRule(key, out _);
            })
            .Select(endpoint => endpoint.DisplayName ?? "(unnamed endpoint)")
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Returns the keys of table rows that no endpoint uses: a stale row, or a renamed route.
    /// </summary>
    /// <param name="endpoints">Every endpoint of the application.</param>
    /// <returns>The keys of the unused rows.</returns>
    public static IReadOnlyList<string> FindUnusedRules(IEnumerable<Endpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var used = endpoints
            .Where(IsMatchable)
            .Select(EndpointKey.Resolve)
            .Where(key => key is not null)
            .ToHashSet(StringComparer.Ordinal);

        return EndpointPermissionTable.Rules
            .Select(rule => rule.Key)
            .Where(key => !used.Contains(key))
            .ToList();
    }

    // Conventional routing also builds one endpoint per route only to generate links: it never matches.
    private static bool IsMatchable(Endpoint endpoint)
        => endpoint.Metadata.GetMetadata<ISuppressMatchingMetadata>()?.SuppressMatching != true;
}
