# Authorization

Authorization is deny-by-default. One requirement, `EndpointPermissionRequirement`, stands behind the **fallback policy**, the **default policy** and the policy the controllers name (`NuGetUserPolicy`), so every endpoint goes through it ([ADR 0002](../adr/0002-authentication-architecture.md), decisions 4 and 5). No controller was changed and `[AllowAnonymous]` is not used anywhere.

## How a request is decided

`EndpointPermissionHandler` (`src/ElPaqueto.Security/Authorization/`):

1. finds the endpoint of the request and its **key**: the route name for controller endpoints, the endpoint name for health, `page:` plus the page path for Razor pages;
2. looks the key up in the permission table (`EndpointPermissionTable`). **No endpoint, no key or no row means denied.**
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

The three flags are **independent**: `Admin` implies neither `Read` nor `Publish`, and `Publish` does not imply `Read`. Static files (`/_content/...`) are served by `UseStaticFiles` before authentication and are the only other thing reachable anonymously.

Health endpoints are mapped as real endpoints (`MapElPaquetoHealthChecks`): the inherited terminal middleware cannot be seen by endpoint authorization and printed the configured services to anyone.

## Adding a route

A route that is not in the table answers 401/403 and the server logs a warning at startup naming it. To open it:

1. add a row to `EndpointPermissionTable` with the least permission it needs and the methods it accepts;
2. the tests `EveryMatchableEndpoint_HasARowInThePermissionTable`, `EveryRowOfThePermissionTable_MatchesARealEndpoint` and `EveryRouteNameOfTheWebProject_HasARowAndNoRowIsInvented` keep the table and the real endpoints in step, and the authorization tests list every route with the permission it needs: add the route there too.

When a change brought by an upstream port adds a controller or a page, these tests fail until a row is added: that is the point.

## What is not covered yet

Writes still accept a Basic-authenticated user holding `Publish`; requiring an API key for writes comes with the API key work. Throttling, HTTPS enforcement and API keys are separate changes, so **the server must still not be exposed to untrusted networks.**
