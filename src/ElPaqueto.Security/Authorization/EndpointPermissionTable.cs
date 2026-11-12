namespace ElPaqueto.Security.Authorization;

/// <summary>
/// What an endpoint requires: the HTTP methods it accepts and the permissions the caller must hold.
/// </summary>
/// <param name="Key">The route name, or <c>page:</c> followed by the Razor page path.</param>
/// <param name="Methods">The methods the row allows. Any other method is refused.</param>
/// <param name="Required">Every flag in this value must be granted. Ignored for anonymous rows.</param>
/// <param name="Anonymous">Whether the endpoint is reachable without authentication.</param>
/// <param name="Action">
/// For controller routes, the <c>Controller.Action</c> the row is meant for. A route name is only a label: an endpoint
/// that reuses it for another action is refused instead of inheriting the row. <see langword="null"/> for pages and health.
/// </param>
/// <param name="RequiresApiKeyHeader">
/// Whether the caller must be authenticated by an API key sent in the <c>X-NuGet-ApiKey</c> header. A user password,
/// or a key sent only as a Basic password, is refused (403) even with the required permissions.
/// </param>
internal sealed record EndpointPermissionRule(
    string Key,
    string[] Methods,
    Permission Required,
    bool Anonymous = false,
    string? Action = null,
    bool RequiresApiKeyHeader = false);

/// <summary>
/// The one table that decides the permission of every endpoint (ADR 0002, decision 5).
/// </summary>
/// <remarks>
/// An endpoint that has no row is denied: a route added later, for example by a ported upstream change,
/// stays closed until somebody adds a row here and the inventory test accepts it. Route names are
/// spelled as literals because this project does not reference the web project; a test that can
/// reference it checks that they match <c>Routes</c>.
/// </remarks>
internal static class EndpointPermissionTable
{
    public const string HealthKey = "health";
    public const string HealthDetailsKey = "health-details";
    public const string DeleteKey = "delete";

    private static readonly string[] Reads = ["GET", "HEAD"];

    private static readonly EndpointPermissionRule[] AllRules =
    [
        // NuGet V3 protocol: reading (issue #25).
        Read("index", "ServiceIndex.Get"),
        Read("search", "Search.Search"),
        Read("autocomplete", "Search.Autocomplete"),
        Read("dependents", "Search.Dependents"),
        Read("registration-index", "PackageMetadata.RegistrationIndex"),
        Read("registration-leaf", "PackageMetadata.RegistrationLeaf"),
        Read("package-versions", "PackageContent.GetPackageVersions"),
        Read("package-download", "PackageContent.DownloadPackage"),
        Read("package-download-manifest", "PackageContent.DownloadNuspec"),
        Read("package-download-readme", "PackageContent.DownloadReadme"),
        Read("package-download-icon", "PackageContent.DownloadIcon"),
        Read("symbol-download", "Symbol.Get"),
        Read("prefixed-symbol-download", "Symbol.Get"),

        // Publishing and deleting (issues #26 and #32): only with an API key sent in X-NuGet-ApiKey. Deleting also
        // needs Admin when packages are deleted for good.
        Write("upload-package", "PUT", "PackagePublish.Upload"),
        Write("upload-symbol", "PUT", "Symbol.Upload"),
        Write("relist", "POST", "PackagePublish.Relist"),
        Write(DeleteKey, "DELETE", "PackagePublish.Delete"),

        // Web pages (issue #27).
        Read("page:/Index"),
        Read("page:/Package"),
        Read("page:/Upload"),
        Read("page:/Statistics"),

        // Health: the liveness endpoint is the only anonymous one and reveals the overall status only.
        new(HealthKey, Reads, Permission.None, Anonymous: true),
        new(HealthDetailsKey, Reads, Permission.Admin),
    ];

    private static readonly Dictionary<string, EndpointPermissionRule> ByKey =
        AllRules.ToDictionary(rule => rule.Key, StringComparer.Ordinal);

    public static IReadOnlyCollection<EndpointPermissionRule> Rules => AllRules;

    public static bool TryGetRule(string key, out EndpointPermissionRule rule)
        => ByKey.TryGetValue(key, out rule!);

    private static EndpointPermissionRule Read(string key, string? action = null) => new(key, Reads, Permission.Read, Action: action);

    private static EndpointPermissionRule Write(string key, string method, string action)
        => new(key, [method], Permission.Publish, Action: action, RequiresApiKeyHeader: true);
}
