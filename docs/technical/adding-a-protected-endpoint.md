# Adding a protected endpoint

Every endpoint is denied until the permission table has a row for it ([authorization](authorization.md)). This is deliberate: a route added by ElPaqueto code, or brought in by a ported upstream change, stays closed until someone decides who may call it. This page is the procedure for that decision. It applies the same way to a controller action, a Razor page and a minimal API endpoint.

## How you notice

- The server logs audit event 1903 `EndpointNotMapped` at startup, naming the endpoint.
- Requests to it get `401` (anonymous) or `403` (authenticated).
- The inventory tests in `tests/ElPaqueto.Tests/AuthorizationIntegrationTests.cs` fail:
  - `EveryMatchableEndpoint_HasARowInThePermissionTable`: an endpoint has no row;
  - `EveryRowOfThePermissionTable_MatchesARealEndpoint`: a row names an endpoint that no longer exists, or the row's `Controller.Action` no longer matches;
  - `EveryRouteNameOfTheWebProject_HasARowAndNoRowIsInvented`: a route name of `src/ElPaqueto.Web/Routes.cs` has no row, or a row names a route that does not exist.

## Steps

1. **Find the endpoint's key.** For a controller route, the route name (`Routes.*RouteName` in `src/ElPaqueto.Web/Routes.cs`, mapped in `ElPaquetoEndpointBuilder`); for a Razor page, `page:` plus the page path (`page:/Index`, or `page:/<Area>/<Page>` in an area); for a named endpoint, its endpoint name.

2. **Choose the least permission.** `Read` for anything that only reads packages or metadata; `Publish` for anything that changes the feed; `Admin` for server information or administration. The flags are independent: `Admin` does not include `Read`. Anonymous access needs a strong reason (only the liveness endpoint has it) and a security review.

3. **Add a row** to `AllRules` in `src/ElPaqueto.Security/Authorization/EndpointPermissionTable.cs`:
   - the key, as a literal (the security project does not reference the web project; the third inventory test checks the spelling);
   - the HTTP methods it accepts, and no more: a read row accepts `GET` and `HEAD` only;
   - for a controller, the `Controller.Action` it is meant for, so that another action reusing the route name is not opened by accident;
   - for any route that changes state, `RequiresApiKeyHeader: true` (the table's `Write` helper sets it), so that only an API key sent in `X-NuGet-ApiKey` can call it. Passwords and keys sent as Basic passwords are then refused, like on the existing write routes.

4. **Add the route to the authorization matrix** in `AuthorizationIntegrationTests`: `ReadRoutes()` or `WriteRoutes()` (method and path). The theories built on them then check, for that route, anonymous `401`, `403` without the permission, success with it, and for writes the refusal of passwords, Basic-sent keys and `Read` keys.

5. **Add behaviour tests** for anything the matrix does not cover: side effects that must not happen before authorization (see `WebUiIntegrationTests.AnonymousPackagePage_WithMirrorEnabled_MakesNoUpstreamRequest`), new parameters, audit events.

6. **Check with a real client** when the route is part of the NuGet protocol (push, restore, delete): run the client against a server, as described in [testing](testing.md).

7. **Document it**: the table in [authorization](authorization.md#the-table), the user documentation if users call it, and the [upstream touch map](upstream-touch-map.md) if an inherited file changed.

8. **Run the whole suite** (`dotnet test ElPaqueto.sln`). The three inventory tests must pass, and no startup warning 1903 must remain.

## Ported upstream changes

A port that adds a controller, an action with a new route name, or a page fails the inventory tests until steps 1 to 8 are done in the same pull request. Never make them pass by removing a test or by adding a row broader than the endpoint needs. The security checklist of [Porting upstream changes](../upstream-porting.md#6-security-review-checklist) lists the other things to review: `[AllowAnonymous]` attributes, middleware order, GET endpoints with side effects.
