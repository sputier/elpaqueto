# Authorization

Authorization is deny-by-default. One requirement, `EndpointPermissionRequirement`, stands behind the **fallback policy**, the **default policy** and the policy the controllers name (`NuGetUserPolicy`), so every endpoint goes through it ([ADR 0002](../adr/0002-authentication-architecture.md), decisions 4 and 5). No controller was changed and `[AllowAnonymous]` is not used anywhere.

## How a request is decided

`EndpointPermissionHandler` (`src/ElPaqueto.Security/Authorization/`):

1. finds the endpoint of the request and its **key**: the route name for controller endpoints, the endpoint name for health, `page:` plus the page path for Razor pages (`page:/<Area>/<Page>` for a page of an area, so that it never takes the row of a root page with the same name);
2. looks the key up in the permission table (`EndpointPermissionTable`). **No endpoint, no key or no row means denied.** A controller row also records the `Controller.Action` it is meant for: a route name is only a label, so an endpoint that reuses `search` for another action is denied instead of inheriting the row. The startup warning and the inventory tests report such an endpoint like an unmapped one.
3. denies a method the row does not allow (`POST /v3/search` is refused: the inherited routes accepted any verb);
4. lets an `Anonymous` row through;
5. otherwise requires an authenticated principal; for a **write row** (`RequiresApiKeyHeader`), a principal authenticated by an API key read from the `X-NuGet-ApiKey` header (`CredentialClaimsExtensions.ApiKeyIdFromHeader`: method `api_key`, credential source `x-nuget-apikey`, exactly one authenticated identity and one key id);
6. and finally that the principal holds **every** permission flag of the row, read from the claims built by authentication. The handler never queries a database, but the claims were built from the user or key rows read at the start of the same request.

A failed requirement for an unauthenticated caller makes the framework answer **401** with the Basic challenge; for an authenticated caller it answers **403** with no challenge. A request that matches no endpoint is denied the same way: an unknown URL is 401 for anonymous callers (it reveals nothing) and 403 for authenticated ones.

## The table

| Key | Methods | Requires |
| --- | --- | --- |
| `index`, `search`, `autocomplete`, `dependents`, `registration-index`, `registration-leaf`, `package-versions`, `package-download`, `package-download-manifest`, `package-download-readme`, `package-download-icon`, `symbol-download`, `prefixed-symbol-download` | GET, HEAD | `Read` |
| `page:/Index`, `page:/Package`, `page:/Upload`, `page:/Statistics` | GET, HEAD | `Read` |
| `upload-package`, `upload-symbol` | PUT | `Publish`, API key from `X-NuGet-ApiKey` |
| `relist` | POST | `Publish`, API key from `X-NuGet-ApiKey` |
| `delete` | DELETE | `Publish`, plus `Admin` when `PackageDeletionBehavior` is `HardDelete`; API key from `X-NuGet-ApiKey` |
| `health` (liveness, `/health`) | GET, HEAD | anonymous, overall status only |
| `health-details` (`/health/details`) | GET, HEAD | `Admin` |
| anything else | | denied |

The table lists the most a route may accept: routing can restrict further. `index`, `registration-index` and `registration-leaf` are mapped for GET only by the inherited code, so a HEAD request to them is answered by the router's 405 endpoint, which has no row and is therefore refused with 401 or 403.

**Writes need an API key in `X-NuGet-ApiKey`** ([ADR 0002](../adr/0002-authentication-architecture.md), decision 6). A user password, or a key sent only as a Basic password, gets **403** on the four write rows even when it holds `Publish` (and `Admin`): least privilege, and a credential that can be revoked per machine. A key's permissions are its effective ones (key ∩ owner's current permissions), so a `Read` key, or a `Publish` key whose owner lost `Publish`, also gets 403. The inherited write actions then run their own inline check, which can only confirm this decision: see [API keys](api-keys.md#the-inline-check-of-the-inherited-write-actions).

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
- A CORS preflight (`OPTIONS` with `Origin`) is answered 401 on every route for now: `UseCors` still runs after authorization. Moving it between routing and authorization is the job of a later change; browsers that send an `Authorization` header cannot use the server until then.
- The decision to require `Admin` for a hard delete reads the deletion behaviour the same way the deletion service does (a per-request snapshot).

## What is not covered yet

A user holding `Publish` can still delete package versions for good through the retention options (`DeleteOldVersionsAsync`) and replace a version's content with `AllowPackageOverwrites`: both are controlled by server settings, and closing them belongs to the write-route work. `/stats` still lists the configured services to every user with `Read` while `ListConfiguredServices` is true. Throttling is a separate change, so **the server must still not be exposed to untrusted networks.**
