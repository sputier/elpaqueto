# Forwarded headers, public base URL and HTTPS policy

This note explains how the server decides the scheme, host and client address of a request, and why. User-facing settings are in [the configuration reference](../user/configuration.md) and [the reverse-proxy guide](../user/reverse-proxy.md).

## Threats

- **Host spoofing.** NuGet clients follow the absolute URLs that the service index (`/v3/index.json`) and registration responses give them, and send their stored credentials with those requests. The inherited server built those URLs from `Host` and `X-Forwarded-Host`, and accepted forwarded headers from any client. A request with `X-Forwarded-Host: attacker.example.com` therefore produced a service index pointing at the attacker; any cache between clients and the server could keep and serve that poisoned index to other users.
- **Scheme spoofing.** `X-Forwarded-Proto: https` from any client made a plain-HTTP request look like HTTPS, defeating any HTTPS check.
- **Address spoofing.** `X-Forwarded-For` from any client replaced the client address, defeating per-address throttling and polluting logs.
- **Credentials in clear text.** Nothing prevented Basic credentials or API keys from being accepted over plain HTTP from a remote client.

## Design

The pipeline starts with `UseElPaquetoForwardedHeaders` (`src/ElPaqueto.Security/ElPaquetoSecurityPipelineExtensions.cs`), in place of the inherited `UseForwardedHeaders`, before `UsePathBase`, static files and authentication. It runs two steps.

1. **ASP.NET Core's forwarded-headers middleware**, configured by `ConfigureTrustedForwardedHeaders`, a post-configuration of `ForwardedHeadersOptions`. A post-configuration runs after every `Configure`, so it replaces the inherited configuration (which cleared the trusted lists, trusting everyone) without editing that inherited file. It sets:
   - the trusted proxies and networks from `Security:ReverseProxy`, or loopback only when none are configured; the configured lists replace the default;
   - `ForwardLimit` from `Security:ReverseProxy:ForwardLimit` (default 1), so a client cannot smuggle extra hops through a trusted proxy;
   - `X-Forwarded-For` and `X-Forwarded-Proto`, plus `X-Forwarded-Host` only when no public base URL is set;
   - an empty `AllowedHosts` list for forwarded hosts (the framework default: any value from a trusted proxy).

   On .NET 10 the trusted networks are set through `KnownIPNetworks`. The obsolete `KnownNetworks` collection is a view of it, so clearing `KnownIPNetworks` also drops anything added through the obsolete name.

2. **`TransportSecurityMiddleware`**, which:
   - rejects the request with `403`, plain text `HTTPS is required.`, `Cache-Control: no-store`, no `WWW-Authenticate` and no `Location`, when HTTPS is required, the scheme is not `https` and the client address is not loopback. Both values are the ones left by step 1, so only a trusted proxy can make a request count as HTTPS or as coming from another address. A loopback proxy relaying a remote client counts as remote. A connection without an IP address (Unix socket, named pipe, in-process test server) counts as local;
   - then, when `Security:PublicBaseUrl` is set, overwrites `Request.Scheme` and `Request.Host` with its values. The check comes first on purpose: otherwise every request would look like HTTPS.

Because the request itself is rewritten, every URL that `ElPaquetoUrlGenerator` produces uses the public base URL, including the two it concatenates by hand from `Request.Scheme`, `Request.Host` and `Request.PathBase`. That inherited file is not changed for this.

The HTTPS decision is made before authentication and before routing, so credentials sent over remote plain HTTP are never verified, whatever route they target and whichever authentication handler is installed. The integration tests prove this by counting calls to the framework's `IAuthenticationService`.

## Decisions

- **403, not 401, 400, 421 or a redirect.** A 401 carries a challenge that invites the client to send credentials over the insecure channel. A redirect arrives after the credentials were already sent and makes many clients send them again, possibly to another origin. 403 states that the request is understood and refused, and its body says why.
- **All remote plain-HTTP requests are refused, not only those with credentials.** An anonymous request would otherwise get a 401 challenge over HTTP and the client would answer it with credentials, in clear text.
- **Loopback only by default.** Trusting all private ranges by default would let any host on a shared private network forge its address and scheme. A deployment with a proxy elsewhere must name it; until it does, remote requests fail closed with 403, and a warning (logged once per process) names the address whose forwarded headers were ignored.
- **No trust-everyone switch.** Catch-all networks (`0.0.0.0/0`, `::/0`, and IPv6 networks containing every IPv4-mapped address) are rejected at startup. The IPv4-mapped check compares address bits directly, because `System.Net.IPNetwork.Contains` does not report IPv4-mapped addresses reliably.
- **Public base URL optional.** It is not mandatory in this version; a startup warning is logged when it is missing and the server listens beyond loopback. Its value must be an absolute `https` URL with no path (the path prefix stays with `PathBase`), query, fragment or user information.

## Not covered here

HSTS and other response headers, CORS, TLS inside Kestrel and `X-Forwarded-Prefix` are out of scope. The `Host` header of requests is not filtered by this module: with `Security:PublicBaseUrl` set it no longer influences generated URLs, and without it the framework's `AllowedHosts` host filtering (enabled by the default host builder, covered by an integration test) is the allow-list.
