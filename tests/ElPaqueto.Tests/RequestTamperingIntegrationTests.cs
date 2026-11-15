using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ElPaqueto.Tests;

/// <summary>
/// Path and verb tampering (issue #30): no other spelling of a protected URL, and no other method, reaches the
/// code of a protected endpoint without the permission its row requires.
/// </summary>
/// <remarks>
/// The requests go through a real Kestrel listener, byte for byte (<see cref="RawHttp"/>): an HTTP client library
/// would normalize dot segments and percent-encoding before the server sees them. Each case checks the answer
/// (never 2xx or 3xx), that nothing behind any endpoint ran, and that the packages and package storage are unchanged.
/// </remarks>
public class RequestTamperingIntegrationTests(KestrelSecuritySuiteHost host) : IClassFixture<KestrelSecuritySuiteHost>
{
    /// <summary>Methods tried on every row: the standard ones, plus one the server cannot know.</summary>
    private static readonly string[] Methods = ["GET", "HEAD", "POST", "PUT", "DELETE", "PATCH", "OPTIONS", "TRACE", "PROPFIND", "FOO"];

    /// <summary>
    /// Credentials that must never reach the endpoint of the variants in <see cref="PathCases"/>: they lack the
    /// permission of the row, or are not valid at all.
    /// </summary>
    public static IEnumerable<object[]> PathCases()
    {
        // (row, credential that lacks its permission): reading, downloading, a page, the Admin-only health details,
        // and the delete route with a key that holds Read but not Publish.
        (string Key, string Credential)[] targets =
        [
            ("index", "password-publish"),
            ("package-download", "key-publish"),
            ("page:/Package", "password-admin"),
            ("page:/Index", "password-none"),
            ("health-details", "key-read"),
            ("delete", "key-read"),
        ];

        foreach (var (key, credential) in targets)
        {
            foreach (var variant in Variants(EndpointSamples.PathOf(key)))
            {
                yield return [key, "anonymous", variant];
                yield return [key, credential, variant];
            }
        }
    }

    /// <summary>Other spellings of <paramref name="path"/>, as an attacker would send them.</summary>
    internal static IEnumerable<string> Variants(string path)
    {
        var (route, query) = path.IndexOf('?') is var q and >= 0 ? (path[..q], path[q..]) : (path, "");
        var segments = route.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var last = segments.Length == 0 ? "" : segments[^1];
        var parent = segments.Length <= 1 ? "" : string.Join('/', segments[..^1]) + "/";

        var variants = new List<string>
        {
            "/" + route.ToUpperInvariant(),
            "/" + string.Concat(route.Select((c, i) => i % 2 == 0 ? char.ToUpperInvariant(c) : c)),
            "/" + route + "/",
            "//" + route,
            "/" + route.Replace("/", "//", StringComparison.Ordinal),
            "/./" + route,
            "/" + parent + "./" + last,
            "/zz/../" + route,
            "/" + parent + "zz/../" + last,
            "/zz/%2e%2e/" + route,
            "/zz/%2E%2E/" + route,
            "/zz/%252e%252e/" + route,
            "/" + route.Replace("/", "%2F", StringComparison.Ordinal),
            "/" + route.Replace("/", "%2f", StringComparison.Ordinal),
            "/" + route.Replace("/", "%252F", StringComparison.Ordinal),
            "/" + route.Replace("/", "\\", StringComparison.Ordinal),
            "/" + route.Replace(".", "%2e", StringComparison.Ordinal),
            "/" + route.Replace(".", "%252e", StringComparison.Ordinal),
            "/" + route + ";x=1",
            "/" + parent + last + ";jsessionid=1",
            "/" + parent.Replace("/", ";x/", StringComparison.Ordinal) + last,
            "/" + route + "%20",
            "/" + route + ".",
            "/" + route + "x",
            "/" + route + "/x",
            "/" + route + "%00",
            "/feed/" + route,
            // Absolute-form targets: one naming this server, one naming another host (Kestrel refuses it: 400).
            Origin + "/" + route,
            "http://localhost/" + route,
        };

        if (route.Length == 0)
        {
            // The home page also answers to its page name.
            variants.AddRange(["/Index", "/index", "/INDEX", "/Index/", "/Index;x=1", "/Index%2F", "/zz/../Index"]);
        }

        return variants.Where(v => v != "/" + route).Distinct(StringComparer.Ordinal).Select(v => v + query);
    }

    /// <summary>Replaced by the scheme and authority of the listener when the request is sent.</summary>
    internal const string Origin = "{origin}";

    /// <summary>
    /// The only spellings Kestrel itself may refuse with 400, before the application sees them: a NUL byte, and an
    /// absolute-form target whose authority is not the Host header. Every other variant must reach authorization.
    /// </summary>
    internal static bool KestrelMayRefuse(string target)
        => target.Contains("%00", StringComparison.Ordinal) || target.StartsWith("http://localhost/", StringComparison.Ordinal);

    [Theory]
    [MemberData(nameof(PathCases))]
    public async Task AnotherSpellingOfAProtectedPath_NeverReachesItsEndpoint(string key, string credential, string target)
    {
        var method = EndpointSamples.MethodOf(key);
        var before = await host.SnapshotStateAsync();
        host.Probe.Clear();

        var response = await RawHttp.SendAsync(host.Address, method, target.Replace(Origin, host.Address.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal), host.HeadersFor(credential));

        AssertRefused(response, credential, $"{method} {target} (row '{key}') with {credential}", before, await host.SnapshotStateAsync(), KestrelMayRefuse(target));
    }

    [Fact]
    public async Task TheCanonicalPaths_AreRefusedToo_SoTheVariantsProveSomething()
    {
        // The same targets and credentials as the variants, spelled canonically: refused for the same reasons.
        foreach (var data in PathCases().Select(row => ((string)row[0], (string)row[1])).Distinct())
        {
            var (key, credential) = data;
            var response = await RawHttp.SendAsync(host.Address, EndpointSamples.MethodOf(key), "/" + EndpointSamples.PathOf(key), host.HeadersFor(credential));

            Assert.True(response.Status is 401 or 403, $"/{EndpointSamples.PathOf(key)} with {credential} answered {response.Status}.");
            Assert.Equal(key, response.Header(RequestProbe.EndpointHeader));
        }
    }

    [Fact]
    public async Task TheVariants_AreStillAnsweredForAnAuthorizedCaller_SoTheyAreNotJustBroken()
    {
        // With every permission, the case and trailing-slash variants still reach the endpoint: they are refused
        // above because of the missing credential, not because they are malformed.
        foreach (var target in new[] { "/V3/INDEX.JSON", "/v3/index.json/", "/zz/../v3/index.json", "/v3/./index.json", "/Index" })
        {
            var response = await RawHttp.SendAsync(host.Address, "GET", target, host.HeadersFor("key-all"));

            Assert.True(response.Status == 200, $"{target} with every permission answered {response.Status}.");
        }
    }

    public static IEnumerable<object[]> VerbCases()
        => from key in EndpointSamples.Keys()
           from method in Methods
           where !AllowedOnThePath(key, method)
           from credential in new[] { "anonymous", "key-all" }
           select new object[] { key, method, credential };

    // Unlisting (DELETE) and relisting (POST) share one URL: each is the other's row's allowed method there.
    private static bool AllowedOnThePath(string key, string method)
        => EndpointSamples.Keys()
            .Where(other => EndpointSamples.PathOf(other) == EndpointSamples.PathOf(key))
            .Any(other => EndpointSamples.Rule(other).Methods.Contains(method));

    [Theory]
    [MemberData(nameof(VerbCases))]
    public async Task AMethodTheRowDoesNotAllow_NeverReachesTheEndpoint(string key, string method, string credential)
    {
        var before = await host.SnapshotStateAsync();
        host.Probe.Clear();

        var response = await RawHttp.SendAsync(host.Address, method, "/" + EndpointSamples.PathOf(key), host.HeadersFor(credential));

        AssertRefused(response, credential, $"{method} /{EndpointSamples.PathOf(key)} (row '{key}') with {credential}", before, await host.SnapshotStateAsync());
    }

    public static IEnumerable<object[]> OverrideCases()
        => from key in EndpointSamples.Keys()
           where EndpointSamples.IsWrite(key)
           from header in new[] { "X-HTTP-Method-Override", "X-HTTP-Method", "X-Method-Override" }
           from method in new[] { "GET", "POST" }
           select new object[] { key, header, method };

    [Theory]
    [MemberData(nameof(OverrideCases))]
    public async Task AMethodOverrideHeader_NeverTurnsARequestIntoAWrite(string key, string header, string method)
    {
        // A key that may read but not publish asks, through the header, for the method of a write row.
        var headers = host.HeadersFor("key-read").Append(new(header, EndpointSamples.MethodOf(key)));
        var before = await host.SnapshotStateAsync();
        host.Probe.Clear();

        var response = await RawHttp.SendAsync(host.Address, method, "/" + EndpointSamples.PathOf(key), headers);

        AssertRefused(response, "key-read", $"{method} /{EndpointSamples.PathOf(key)} with {header}: {EndpointSamples.MethodOf(key)} (row '{key}')", before, await host.SnapshotStateAsync());
    }

    [Theory]
    [InlineData("X-HTTP-Method-Override")]
    [InlineData("X-HTTP-Method")]
    [InlineData("X-Method-Override")]
    public async Task AMethodOverrideHeader_NeverTurnsAStateChangingMethodIntoARead(string header)
    {
        // Anonymous, a POST that claims to be a GET: the read row does not open to it.
        var before = await host.SnapshotStateAsync();
        host.Probe.Clear();

        var response = await RawHttp.SendAsync(host.Address, "POST", "/v3/index.json", [new(header, "GET")]);

        AssertRefused(response, "anonymous", $"POST /v3/index.json with {header}: GET", before, await host.SnapshotStateAsync());
        Assert.Equal(401, response.Status);
    }

    [Fact]
    public async Task ACorsPreflight_IsAnsweredWithoutRunningTheEndpointOrReturningData()
    {
        // A preflight is the one anonymous OPTIONS the server answers itself (CORS runs before authorization): it
        // must stay empty and never reach the code behind the URL.
        host.Probe.Clear();

        var response = await RawHttp.SendAsync(
            host.Address,
            "OPTIONS",
            "/v3/package/testdata/1.2.3/testdata.1.2.3.nupkg",
            [new("Origin", "https://attacker.example"), new("Access-Control-Request-Method", "GET")]);

        Assert.Equal(204, response.Status);
        Assert.Empty(response.Body);
        Assert.Null(response.Header("Access-Control-Allow-Origin"));
        Assert.Empty(host.Probe.Executed);
    }

    /// <summary>
    /// The answer must be the refusal of the authorization layer itself: 401 without a valid credential, 403 with one.
    /// A 404 or 405 is not accepted: deny-by-default answers 401/403 even when no endpoint matches, so a 404 would
    /// mean that authorization let the request through. A 400 is accepted only for the targets of
    /// <see cref="KestrelMayRefuse"/>, and only when it comes from Kestrel (the application never saw the request).
    /// </summary>
    internal static void AssertAuthorizationRefusal(RawHttp.Response response, string credential, string scenario, bool kestrelMayRefuse = false)
    {
        var expected = SecuritySuiteHost.Credential(credential).Valid ? 403 : 401;
        var endpoint = response.Header(RequestProbe.EndpointHeader);
        Assert.True(
            response.Status == expected || (kestrelMayRefuse && response.Status == 400 && endpoint is null),
            $"{scenario}: answered {response.Status} (endpoint {endpoint ?? "not reached"}); expected {expected}.");
    }

    private void AssertRefused(RawHttp.Response response, string credential, string scenario, string before, string after, bool kestrelMayRefuse = false)
    {
        AssertAuthorizationRefusal(response, credential, scenario, kestrelMayRefuse);
        Assert.True(host.Probe.Executed.Count == 0, $"{scenario}: answered {response.Status}, and this ran: {string.Join(", ", host.Probe.Executed)}.");
        Assert.True(before == after, $"{scenario}: the packages or package storage changed.");
    }
}

/// <summary>
/// Path tampering when the server is mounted below a path base: neither the prefix nor its absence opens a
/// protected endpoint.
/// </summary>
public class PathBaseTamperingIntegrationTests(PathBaseSecuritySuiteHost host) : IClassFixture<PathBaseSecuritySuiteHost>
{
    public static IEnumerable<object[]> Cases()
    {
        const string Base = PathBaseSecuritySuiteHost.PathBase;
        string[] targets =
        [
            Base + "/v3/index.json",
            "/v3/index.json",
            Base.ToUpperInvariant() + "/v3/index.json",
            Base + "//v3/index.json",
            "/" + Base + "/v3/index.json",
            Base + "/./v3/index.json",
            Base + "/zz/../v3/index.json",
            Base + "x/v3/index.json",
            Base + "%2Fv3/index.json",
            Base + "%2fv3%2findex.json",
            Base + ";x=1/v3/index.json",
            Base + "/../v3/index.json",
            Base + "/%2e%2e/feed/v3/index.json",
            Base + Base + "/v3/index.json",
            Base + "/health/details",
            Base + "/HEALTH/DETAILS",
            Base + "/health/details/",
            Base + "/Index",
            Base,
            Base + "/",
        ];

        foreach (var target in targets)
        {
            yield return [target, "anonymous"];
            yield return [target, "password-publish"];
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task NoSpellingOfThePathBase_OpensAProtectedEndpoint(string target, string credential)
    {
        host.Probe.Clear();

        var response = await RawHttp.SendAsync(host.Address, "GET", target, host.HeadersFor(credential));

        RequestTamperingIntegrationTests.AssertAuthorizationRefusal(response, credential, $"GET {target} with {credential}");
        Assert.True(host.Probe.Executed.Count == 0, $"GET {target} with {credential}: this ran: {string.Join(", ", host.Probe.Executed)}.");
    }

    [Fact]
    public async Task ThePathBase_StillServesAnAuthorizedCaller()
    {
        var response = await RawHttp.SendAsync(host.Address, "GET", PathBaseSecuritySuiteHost.PathBase + "/v3/index.json", host.HeadersFor("key-all"));

        Assert.Equal(200, response.Status);
        Assert.Contains("/feed/v3/", response.Body, StringComparison.Ordinal);
    }
}
