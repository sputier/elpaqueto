# Web security: CORS, response headers and the web UI

This note describes what the server does to protect browsers that use the web UI or call the API from a script, and why. User-facing settings are in [the configuration reference](../user/configuration.md).

## Request pipeline

`Startup.Configure` runs, in this order ([ADR 0002](../adr/0002-authentication-architecture.md), "Request flow"):

1. `UseElPaquetoSecurityHeaders`: registers a callback that sets the security headers when the response starts;
2. `UseElPaquetoForwardedHeaders`: trusted forwarded headers and the HTTPS policy ([forwarded headers](forwarded-headers.md));
3. `UsePathBase`, `UseStaticFiles`, `UseAuthentication`, `UseRouting`;
4. `UseElPaquetoCors`: the CORS policy;
5. `UseAuthorization`, then the endpoints.

The headers middleware comes first so that every response gets its headers, including the 403 of the HTTPS policy, static files and the 401/403 of authorization. Because the headers are decided when the response starts, they see the final status code and content type, and the **effective** scheme: the one left after trusted forwarded headers and `Security:PublicBaseUrl`.

## CORS

NuGet clients are not browsers, and the web UI is served from the server's own origin, so the server allows **no cross-origin access by default**. The inherited policy (`AllowAnyOrigin`, any method, any header, applied after authorization) is removed from `ConfigureElPaquetoServer`; the only policy is built by `ConfigureElPaquetoCors` from `Security:Cors`:

- exact origins only (`Security:Cors:AllowedOrigins`), normalized to the form browsers send (`https://host[:port]`, lower case, ASCII host); wildcards, `null`, paths and user information are rejected at startup;
- methods from `Security:Cors:AllowedMethods` (default `GET`, `HEAD`; only the methods the routes use are accepted) and request headers from `Security:Cors:AllowedHeaders` (default `Authorization`; `*` is rejected);
- **credentials are never allowed** (`Access-Control-Allow-Credentials` is never sent). A browser keeps the Basic credentials a user typed for the web UI and attaches them to any request to the server when the calling script asks for credentials mode; with that header, a script of an allowed origin could read anything the user can read without knowing the password. A script that needs to authenticate sets the `Authorization` header itself, which works without credentials mode.

`UseElPaquetoCors` runs after routing and **before authorization**: a preflight (`OPTIONS` with `Origin` and `Access-Control-Request-Method`) carries no credentials and would otherwise be refused with 401. The CORS middleware answers every preflight itself with `204`, before authorization and without reaching an endpoint; the answer carries the `Access-Control-Allow-*` headers only when the origin, the method and the headers are allowed. Other requests continue to authorization as usual; for an allowed origin the response, including a 401 or 403, carries `Access-Control-Allow-Origin` so that the script can read the status.

## Response headers

| Header | Responses | Value |
| --- | --- | --- |
| `X-Content-Type-Options` | all | `nosniff` |
| `Referrer-Policy` | all | `no-referrer`: package pages must not reveal private package names to icon, project or license hosts |
| `X-Frame-Options` | all | `DENY` |
| `Content-Security-Policy` | HTML (`text/html`, `application/xhtml+xml`) | the page policy below |
| `Content-Security-Policy` | everything else, except `304 Not Modified` | `default-src 'none'; frame-ancestors 'none'; sandbox` |
| `Cache-Control` | HTML, and every response with status 400 or above | `no-store` (replaces any other value) |
| `Strict-Transport-Security` | only when `Security:Hsts:Enabled`, the effective scheme is HTTPS and the host is not `localhost` or a loopback address | `max-age=<Security:Hsts:MaxAgeSeconds>[; includeSubDomains]` |

Package downloads, manifests, registration and search JSON, symbols and static files keep their caching: the server adds no `Cache-Control` to them. Clients rely on their own caches, and a shared cache must not store a response to a request that carries `Authorization` unless the response explicitly allows it (RFC 9111, section 3.5), which the server never does. Errors and pages are `no-store` so that no cache keeps a challenge, a refusal or a page rendered for one user. The health endpoint sets its own `no-store, no-cache`.

The resource policy only matters when such a response is opened as a document, for example an SVG package icon opened directly in a tab: nothing it contains can run, load other resources or be framed. It has no effect when the resource is used by a page (an image, a script, a stylesheet). A `304 Not Modified` gets no policy: it has no content type, and browsers merge its headers into the cached response, which keeps the policy it was stored with.

HSTS is off by default: TLS is usually terminated by a reverse proxy, which may already send it, and a wrong policy is remembered by browsers for its whole duration. It is never sent for `localhost`, because browsers would then force HTTPS on every local development server.

### The page policy

```text
default-src 'self';
script-src 'self' 'unsafe-inline' 'unsafe-eval';
style-src 'self';
img-src 'self' data: https:;
font-src 'self' https://static2.sharepointonline.com https://res-1.cdn.office.net;
connect-src 'self';
object-src 'none';
base-uri 'self';
form-action 'self';
frame-ancestors 'none'
```

It fits the current UI, which was not changed:

- `'unsafe-eval'`: Alpine.js 2.8.1 compiles its directives (`x-data`, `@click`, ...) with `new Function`;
- `'unsafe-inline'` scripts: inline `<script>` blocks (`Package.cshtml`, `Upload.cshtml`) and event handler attributes (`onclick` in `Index.cshtml`, `onerror` icon fallback in `Index.cshtml` and `Package.cshtml`);
- `img-src https:`: package icons come from the `IconUrl` of each package, on any host;
- `font-src`: `fabric.min.css` loads its icon font from `static2.sharepointonline.com`, which redirects to `res-1.cdn.office.net`; a policy also checks the target of a redirect.

What it **does** protect against:

- clickjacking: no page can be framed by another site (`frame-ancestors 'none'`, plus `X-Frame-Options` for older browsers);
- scripts, styles and fonts from other hosts, `<object>`/`<embed>` plugins, a `<base>` tag that redirects relative URLs, and forms posting to another site;
- `fetch`/`XMLHttpRequest`/WebSocket connections to other hosts from an injected script.

What it **does not** protect against:

- an injected inline script or event handler, or an injected Alpine.js directive: `'unsafe-inline'` and `'unsafe-eval'` allow them. The protection against stored script is therefore that the server never emits attacker-controlled markup (Razor encoding, and the Markdown rules below);
- data exfiltration through image URLs: `img-src https:` lets an injected script or tag send data to any https host;
- the privacy of icon loading: the browser contacts every icon host; with `no-referrer` it does not say which page it came from, but the host still learns the client address.

Removing `'unsafe-inline'` and `'unsafe-eval'` needs UI changes (moving inline scripts to files, replacing or upgrading Alpine.js) and is a follow-up.

The policy was checked with the UI loaded in Chromium 145 (Playwright) against the real pipeline on Kestrel: search, the prerelease toggle (inline `onclick`), the icon fallback (inline `onerror`) on the list and package pages, the install tabs and copy button, the expandable sections, the version list toggle, the README and release notes, the upload page tabs and the statistics page, with no policy violation reported. Framing a page from another document was blocked by `frame-ancestors`.

In Development, the developer exception page is an HTML response too and gets the page policy: its inline styles are blocked, so it renders without styling.

## Package README and release notes

READMEs and release notes come from anyone who can publish a package, or from a mirrored upstream package, and are rendered on the package page for every user, including administrators. `PackageMarkdown.CreatePipeline` (`src/ElPaqueto.Web/Markdown/`) builds the Markdig pipeline:

- `DisableHtml()`: raw HTML (`<script>`, `<img onerror=...>`, `<iframe>`, ...) is rendered as text;
- the generic attributes extension of `UseAdvancedExtensions` is removed: `[x](url){onclick=...}` or `{x-init=...}` would put any attribute on an element, including event handlers and the Alpine.js directives that the page evaluates inside its `x-data` sections;
- link and image URLs are allow-listed after parsing: a link keeps its URL only when it is relative or uses `http`, `https` or `mailto`, an image only when it is relative or uses `http` or `https`. Anything else (`javascript:`, `vbscript:`, `data:`, `file:`, ...) becomes an empty URL, and such an autolink (`<javascript:...>`) is rendered as text. The scheme is read the way browsers read it: leading and trailing control characters and spaces are ignored, and tabs and line breaks are ignored anywhere. Markdig decodes character references before the check, so `&#106;avascript:` is caught too.

The rest of the advanced extensions (tables, task lists, footnotes, emphasis extras, heading identifiers, ...) still work. READMEs that relied on embedded HTML show it as text.

## Web UI access

The pages `/`, `/packages/{id}/{version?}`, `/upload` and `/stats` need `Read` ([authorization](authorization.md)). A browser receives the Basic challenge and shows its own credential prompt; there is no login form, no cookie session and no logout: the browser keeps the credentials until it is closed (or its saved credentials are cleared). Static files under `/_content/` are served without authentication.

The statistics page lists the configured database, storage and search services only when `Statistics:ListConfiguredServices` is `true`; the shipped `appsettings.json` sets it to `false`. Nothing reachable anonymously shows them: the anonymous health endpoint returns the overall status only.

With `Mirror` enabled, opening a package page can fetch the package from the upstream source. This happens in the page handler, after authorization: an anonymous request is answered 401, and a user without `Read` gets 403, before anything is fetched.

## Tests

- `tests/ElPaqueto.Tests/CorsIntegrationTests.cs`: no approval by default, an allowed origin's preflight answered 204 without credentials and without 401, other and near-miss origins refused, methods and headers outside the list, no `Access-Control-Allow-Credentials`, CORS headers on a 401.
- `tests/ElPaqueto.Tests/SecurityHeadersIntegrationTests.cs`: headers on every page, protocol responses (JSON, `.nupkg`, `.nuspec`, registration), static files, 401, 403, 404 and the HTTPS-policy 403; caching left alone where it must be; HSTS off by default, on only over the effective HTTPS (direct, trusted proxy, public base URL), never for an untrusted proxy's claim or `localhost`.
- `tests/ElPaqueto.Tests/WebUiIntegrationTests.cs`: no upstream request for an anonymous or unauthorized package page with `Mirror` on, the statistics page with the shipped settings, and a real package whose README and release notes carry script payloads.
- `tests/ElPaqueto.Web.Tests/Pages/PackageMarkdownFacts.cs` and `PackageModelFacts.cs`: the Markdown rules, payload by payload.
- `tests/ElPaqueto.Security.Tests/SecurityHeadersOptionsTests.cs`, `SecurityHeadersMiddlewareTests.cs`, `SecurityConfigurationHostTests.cs`: validation of `Security:Cors` and `Security:Hsts`, the header rules and the CORS policy shape.
