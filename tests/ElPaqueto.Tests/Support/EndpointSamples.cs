using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using ElPaqueto.Security.Authorization;

namespace ElPaqueto.Tests;

/// <summary>
/// One concrete request for each row of the permission table, used by the security suites.
/// </summary>
/// <remarks>
/// The suites enumerate <see cref="EndpointPermissionTable.Rules"/>, not this list: a row added to the table
/// without a sample here fails <c>EveryRowOfThePermissionTable_HasASampleRequest</c>, and a sample that does not
/// reach its row fails <c>EverySample_IsRoutedToItsOwnRow</c>. The samples name the package that
/// <see cref="Package"/> describes, which the suites publish first, so authorized reads find something.
/// </remarks>
internal static class EndpointSamples
{
    /// <summary>The id of the package the samples read, write and delete.</summary>
    public const string PackageId = "testdata";

    /// <summary>The version of that package.</summary>
    public const string PackageVersion = "1.2.3";

    private const string PdbSignature = "16F71ED8DD574AA2AD4A22D29E9C981Bffffffff";

    private static readonly Dictionary<string, string> Paths = new(StringComparer.Ordinal)
    {
        ["index"] = "v3/index.json",
        ["search"] = "v3/search?q=" + PackageId,
        ["autocomplete"] = "v3/autocomplete?q=test",
        ["dependents"] = "v3/dependents?packageId=" + PackageId,
        ["registration-index"] = $"v3/registration/{PackageId}/index.json",
        ["registration-leaf"] = $"v3/registration/{PackageId}/{PackageVersion}.json",
        ["package-versions"] = $"v3/package/{PackageId}/index.json",
        ["package-download"] = $"v3/package/{PackageId}/{PackageVersion}/{PackageId}.{PackageVersion}.nupkg",
        ["package-download-manifest"] = $"v3/package/{PackageId}/{PackageVersion}/{PackageId}.nuspec",
        ["package-download-readme"] = $"v3/package/{PackageId}/{PackageVersion}/readme",
        ["package-download-icon"] = $"v3/package/{PackageId}/{PackageVersion}/icon",
        ["symbol-download"] = $"api/download/symbols/testdata.pdb/{PdbSignature}/testdata.pdb",
        ["prefixed-symbol-download"] = $"api/download/symbols/ab/testdata.pdb/{PdbSignature}/testdata.pdb",
        ["upload-package"] = "api/v2/package",
        ["upload-symbol"] = "api/v2/symbol",
        ["relist"] = $"api/v2/package/{PackageId}/{PackageVersion}",
        ["delete"] = $"api/v2/package/{PackageId}/{PackageVersion}",
        ["page:/Index"] = "",
        ["page:/Package"] = "packages/" + PackageId,
        ["page:/Upload"] = "upload",
        ["page:/Statistics"] = "stats",
        [EndpointPermissionTable.HealthKey] = "health",
        [EndpointPermissionTable.HealthDetailsKey] = "health/details",
    };

    /// <summary>The keys of the table rows that have no sample: must be empty.</summary>
    public static IReadOnlyList<string> RowsWithoutSample()
        => EndpointPermissionTable.Rules.Select(rule => rule.Key).Where(key => !Paths.ContainsKey(key)).ToList();

    /// <summary>The samples that name no row of the table: must be empty.</summary>
    public static IReadOnlyList<string> SamplesWithoutRow()
        => Paths.Keys.Where(key => !EndpointPermissionTable.TryGetRule(key, out _)).ToList();

    /// <summary>The row of a key.</summary>
    public static EndpointPermissionRule Rule(string key)
        => EndpointPermissionTable.TryGetRule(key, out var rule)
            ? rule
            : throw new InvalidOperationException($"The permission table has no row '{key}'.");

    /// <summary>The path (relative to the root, query included) of the sample of a row.</summary>
    public static string PathOf(string key)
        => Paths.TryGetValue(key, out var path)
            ? path
            : throw new InvalidOperationException($"The row '{key}' of the permission table has no sample request in {nameof(EndpointSamples)}.");

    /// <summary>The method the sample of a row uses: the first one the row allows.</summary>
    public static string MethodOf(string key) => Rule(key).Methods[0];

    /// <summary>Every row key, taken from the table.</summary>
    public static IEnumerable<string> Keys() => EndpointPermissionTable.Rules.Select(rule => rule.Key);

    /// <summary>Whether a row protects a write: its methods change state.</summary>
    public static bool IsWrite(string key) => !Rule(key).Methods.All(method => method is "GET" or "HEAD");

    /// <summary>Builds the sample request of a row with <paramref name="method"/>, a body included for the uploads.</summary>
    public static HttpRequestMessage Request(string key, string method = null)
    {
        method ??= MethodOf(key);
        var request = new HttpRequestMessage(new HttpMethod(method), PathOf(key));
        request.Content = key switch
        {
            "upload-package" => Upload("package", TestResources.Package),
            "upload-symbol" => Upload("symbol", TestResources.SymbolPackage),
            _ when method is "PUT" or "POST" or "PATCH" => new ByteArrayContent([]),
            _ => null,
        };

        return request;
    }

    private static MultipartFormDataContent Upload(string name, string resource)
    {
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(TestResources.GetResourceStream(resource).ToArray()), name, resource);
        return content;
    }
}
