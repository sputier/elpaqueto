# Authorization

Authorization is deny-by-default. One requirement, `EndpointPermissionRequirement`, stands behind the **fallback policy**, the **default policy** and the policy the controllers name (`NuGetUserPolicy`), so every endpoint goes through it ([ADR 0002](../adr/0002-authentication-architecture.md), decisions 4 and 5). No controller was changed and `[AllowAnonymous]` is not used anywhere.

## How a request is decided

`EndpointPermissionHandler` (`src/ElPaqueto.Security/Authorization/`):

1. finds the endpoint of the request and its **key**: the route name for controller endpoints, the endpoint name for health, `page:` plus the page path for Razor pages (`page:/<Area>/<Page>` for a page of an area, so that it never takes the row of a root page with the same name);
2. looks the key up in the permission table (`EndpointPermissionTable`). **No endpoint, no key or no row means denied.** A controller row also records the `Controller.Action` it is meant for: a route name is only a label, so an endpoint that reuses `search` for another action is denied instead of inheriting the row. The startup warning and the inventory tests report such an endpoint like an unmapped one.
3. denies a method the row does not allow (`POST /v3/search` is refused: the inherited routes accepted any verb);
4. lets an `Anonymous` row through;
5. otherwise requires an authenticated principal that holds **every** permission flag of the row, read from the claims built by authentication. The handler never queries a database, but the claims were built from the user row read at the start of the same request.

A failed requirement for an unauthenticated caller makes the framework answer **401** with the Basic challenge; for an authenticated caller it answers **403** with no challenge. A request that matches no endpoint is denied the same way: an unknown URL is 401 for anonymous callers (it reveals nothing) and 403 for authenticated ones.

## The table

| Key | Methods | Requires |
| --- | --- | --- |
| `index`, `search`, `autocomplete`, `dependents`, `registration-index`, `registration-leaf`, `package-versions`, `package-download`, `package-download-manifest`, `package-download-readme`, `package-download-icon`, `symbol-download`, `prefixed-symbol-download` | GET, HEAD | `Read` |
| `page:/Index`, `page:/Package`, `page:/Upload`, `page:/Statistics` | GET, HEAD | `Read` |
| `upload-package`, `upload-symbol` | PUT | `Publish` |
| `relist` | POST | `Publish` |
| `delete` | DELETE | `Publish`, plus `Admin` when `PackageDeletionBehavior` is `HardDelete` |
| `health` (liveness, `/health`) | GET, HEAD | anonymous, overall status only |
| `health-details` (`/health/details`) | GET, HEAD | `Admin` |
| anything else | | denied |

The table lists the most a route may accept: routing can restrict further. `index`, `registration-index` and `registration-leaf` are mapped for GET only by the inherited code, so a HEAD request to them is answered by the router's 405 endpoint, which has no row and is therefore refused with 401 or 403.

The three flags are **independent**: `Admin` implies neither `Read` nor `Publish`, and `Publish` does not imply `Read`. Static files (`/_content/...`) are served by `UseStaticFiles` before authentication and are the only other thing reachable anonymously.

Health endpoints are mapped as real endpoints (`MapElPaquetoHealthChecks`): the inherited terminal middleware cannot be seen by endpoint authorization and printed the configured services to anyone.

## Adding a route

A route that is not in the table answers 401/403 and the server logs a warning at startup naming it. To open it:

1. add a row to `EndpointPermissionTable` with the least permission it needs and the methods it accepts;
2. the tests `EveryMatchableEndpoint_HasARowInThePermissionTable`, `EveryRowOfThePermissionTable_MatchesARealEndpoint` and `EveryRouteNameOfTheWebProject_HasARowAndNoRowIsInvented` keep the table and the real endpoints in step, and the authorization tests list every route with the permission it needs: add the route there too.

When a change brought by an upstream port adds a controller or a page, these tests fail until a row is added: that is the point.

## Behaviour changes to know

- The symbol server downloads (`symbol-download`, `prefixed-symbol-download`) need `Read` and therefore Basic credentials: a debugger or a tool that fetches symbols must be configured with them. How a given debugger answers a Basic challenge is not verified here.
- The health path must name a path below the root (default `/health`): `/` is rejected at startup because the root belongs to the home page, and the details endpoint is `<path>/details`.
- A CORS preflight (`OPTIONS` with `Origin` and `Access-Control-Request-Method`) never reaches authorization: `UseElPaquetoCors` runs between routing and authorization and answers it with `204`, with CORS approval only for an origin listed in `Security:Cors:AllowedOrigins` (none by default). See [web security](web-security.md).
- The decision to require `Admin` for a hard delete reads the deletion behaviour the same way the deletion service does (a per-request snapshot).

## What is not covered yet

Writes still accept a Basic-authenticated user holding `Publish`; requiring an API key for writes comes with the API key validation. A user holding `Publish` can still delete package versions for good through the retention options (`DeleteOldVersionsAsync`) and replace a version's content with `AllowPackageOverwrites`: both are controlled by server settings, and closing them belongs to the write-route work. `/stats` lists the configured services to every user with `Read` only when `Statistics:ListConfiguredServices` is set to `true` (the shipped `appsettings.json` sets it to `false`). Throttling is a separate change, so **the server must still not be exposed to untrusted networks.**
