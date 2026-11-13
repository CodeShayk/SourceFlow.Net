# SourceFlow.AspNetCore

`SourceFlow.AspNetCore` puts a SourceFlow service on HTTP. It maps commands to `POST` endpoints, exposes the
service's named domain queries and its per-service query API, and works with the SourceFlow health checks and the
`dotnet sourceflow` CLI hook in a web host. The package is thin: binding, status codes, limits and security headers
live here, and everything else (deciding, storing, querying) is the core runtime in
[`SourceFlow.Net`](SourceFlow.Net-README.md).

| | |
|---|---|
| Package | `SourceFlow.AspNetCore` |
| Target | `net10.0` only |
| Depends on | `SourceFlow.Net`, the `Microsoft.AspNetCore.App` shared framework |
| Namespace | `SourceFlow.AspNetCore` |
| AOT | Trim- and AOT-compatible. Endpoints are mapped as `RequestDelegate`s, with no reflection-based parameter binding. |

## Contents

- [Installation](#installation)
- [Quick start](#quick-start)
- [Command endpoints](#command-endpoints)
- [Named domain queries over HTTP](#named-domain-queries-over-http)
- [The per-service query API](#the-per-service-query-api)
- [Health checks](#health-checks)
- [The CLI inside a web host](#the-cli-inside-a-web-host)
- [Configuration reference](#configuration-reference)
- [Operational notes](#operational-notes)
- [Troubleshooting](#troubleshooting)
- [Links](#links)

## Installation

```bash
dotnet add package SourceFlow.Net
dotnet add package SourceFlow.AspNetCore
dotnet add package SourceFlow.Storage.PostgreSQL   # or SourceFlow.Storage.SqlServer
```

Use a web project (`Microsoft.NET.Sdk.Web`) that targets `net10.0`.

## Quick start

This is the Bank sample's host, [`samples/Bank/Bank.Api/Program.cs`](../../../samples/Bank/Bank.Api/Program.cs),
trimmed to the HTTP parts:

```csharp
using Bank.Accounts;
using Bank.Contracts;
using Bank.Notifications;
using Bank.Transfers;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using SourceFlow.AspNetCore;
using SourceFlow.Hosting;
using SourceFlow.Storage.PostgreSQL;

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);
string connectionString = builder.Configuration.GetConnectionString("SourceFlow")
    ?? throw new InvalidOperationException("Set ConnectionStrings__SourceFlow to a PostgreSQL connection string.");

builder.Services.AddSourceFlow(sf =>
{
    sf.ServiceName = "bank";
    sf.AddModule<ContractsModule>();
    sf.AddModule<AccountsModule>();
    sf.AddModule<TransfersModule>();
    sf.AddModule<NotificationsModule>();
    sf.UsePostgreSql(connectionString);
    sf.UseInMemoryTransport();
});

builder.Services.AddHealthChecks().AddSourceFlow();

WebApplication app = builder.Build();

// `dotnet run -- sourceflow <command>` runs the CLI with this service's model and storage (ADR-031).
if (await SourceFlow.Tool.SourceFlowCli.TryRunAsync(args, app.Services) is { } exitCode)
{
    return exitCode;
}

app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains(SourceFlowHealthChecks.ReadyTag) });

app.MapCommand<OpenAccount>("/accounts/{accountId}");
app.MapCommand<Deposit>("/accounts/{accountId}/deposits");
app.MapCommand<Withdraw>("/accounts/{accountId}/withdrawals");
app.MapCommand<CloseAccount>("/accounts/{accountId}/close");
app.MapCommand<RequestTransfer>("/transfers/{transferId}");

app.Run();
return 0;
```

Send a command:

```bash
curl -i -X POST http://localhost:8080/accounts/0193a7a0-0000-7000-8000-000000000001 \
  -H 'Content-Type: application/json' -d '{ "holder": "Ada", "initialDeposit": 100 }'
```

```http
HTTP/1.1 200 OK
ETag: "1"
SourceFlow-Consistency: 42:0
Content-Type: application/json; charset=utf-8

{"status":"Accepted","version":1,"error":null}
```

The `SourceFlow.Tool` reference that `TryRunAsync` needs is optional; see
[The CLI inside a web host](#the-cli-inside-a-web-host).

## Command endpoints

```csharp
public static IEndpointConventionBuilder MapCommand<TCommand>(this IEndpointRouteBuilder endpoints, string pattern)
    where TCommand : class, ICommand;
```

`MapCommand<TCommand>(pattern)` maps `POST {pattern}` to one command type. The endpoint is named after the command type
(`WithName(typeof(TCommand).Name)`), and the returned builder accepts the usual conventions, for example
`.RequireAuthorization("tellers")` or `.RequireRateLimiting(...)`.

### Binding

1. The body is read as a JSON object. An empty body (`Content-Length: 0`) counts as `{}`.
2. Every route value is added to the object **when the body does not already have that member**. Route keys are
   converted with the command's JSON naming policy (camelCase by default), and the comparison with body members ignores
   case. So `/accounts/{accountId}` fills `accountId`, and a body that already carries `accountId` wins.
3. The object is deserialised with the command's source-generated `JsonTypeInfo` from the message type registry: the
   same contract the runtime uses, with no reflection.

| Request header | Becomes | Notes |
|----------------|---------|-------|
| `Idempotency-Key` | `SendOptions.IdempotencyKey` | A repeat with the same key returns the original result (`Duplicate`), not a second write. The header name is `SourceFlowHeaders.IdempotencyKey`. |
| `If-Match` | `SendOptions.ExpectedVersion` | A stream version ETag, for example `"3"` or `W/"3"`. Anything else is `400`. A mismatch is `412`. |

### Responses

The command is sent through `ICommandSender.SendAsync`, and the `CommandResult` is mapped as follows:

| `CommandStatus` | HTTP | Body | Headers |
|-----------------|------|------|---------|
| `Accepted`, `NoOp`, `Duplicate` | `200 OK` | `CommandResponse` JSON: `{"status", "version", "error"}` | `ETag: "<stream version>"`, `SourceFlow-Consistency` |
| `Dispatched` (the owner is another service) | `202 Accepted` | none | `SourceFlow-Consistency` when a token was produced |
| `Rejected` (a business rejection, ADR-018) | `422 Unprocessable Entity` | Problem details: `title` = the error code, `detail` = its message, `type` = `urn:sourceflow:error:<code>` | |
| `Conflict` | `409 Conflict`, or `412 Precondition Failed` when the request carried `If-Match` | Problem details: `title` = the error code (`Error.ConcurrencyConflict.Code` by default) | |
| Body is not a JSON object, cannot be read as the command, or `If-Match` is malformed | `400 Bad Request` | Problem details, `title` = `sourceflow.invalid_request` | |

`error` in the `200` body is set only for a `Duplicate` of a request that originally failed, so a client can tell a
repeated failure from a repeated success.

`SourceFlow-Consistency` (`SourceFlowHeaders.Consistency`) carries the `ConsistencyToken` of the write: the global
position of the last event, as `{commit}:{sequence}`, or `{commit}:{sequence}@{tenant}` in a multi-tenant service. A
client that wants to read its own write passes it to a query with `consistency = at_least(@token)`; see
[Architecture/08 Query side and SFQL](Architecture/08-Query-Side-and-SFQL.md#consistency).

### Example: optimistic concurrency from a client

```bash
# Read the current version from the previous response's ETag, then make the write conditional on it.
curl -i -X POST http://localhost:8080/accounts/$ID/withdrawals \
  -H 'Content-Type: application/json' -H 'If-Match: "3"' -H 'Idempotency-Key: 7f6c1c2e' \
  -d '{ "amount": 25 }'
```

Without `If-Match` the command processor re-decides automatically after a concurrency conflict
(`SourceFlowOptions.MaxConcurrencyRetries`, ADR-008), so `409` only appears when the retries are exhausted. With
`If-Match` the client has asked for a specific version, so a stale version is reported as `412` straight away.

### A multi-service host

[`samples/OrderFulfilment/OrderFulfilment.Host/Program.cs`](../../../samples/OrderFulfilment/OrderFulfilment.Host/Program.cs)
runs one of four services per process and maps commands only for the service that owns them:

```csharp
if (service == OrdersService.Name)
{
    app.MapCommand<PlaceOrder>("/orders/{orderId}");
    app.MapCommand<ApproveOrder>("/orders/{orderId}/approve");
    app.MapCommand<RejectOrder>("/orders/{orderId}/reject");
    app.MapCommand<CorrectAddress>("/orders/{orderId}/address");
}
```

You can also map a command that another service owns. Routing sends it through the outbox and the endpoint answers
`202 Accepted`.

## Named domain queries over HTTP

A named domain query is a versioned query contract declared with `[DomainQuery]` and `IDomainQuery<TResult>` (ADR-030).
It is the stable way for other services and UIs to read a service's data. The Bank sample declares one in
[`samples/Bank/Bank.Accounts/AccountSummary.cs`](../../../samples/Bank/Bank.Accounts/AccountSummary.cs):

```csharp
[DomainQuery("accounts.low-balance", 1)]
public sealed partial record LowBalanceAccounts(decimal Below) : IDomainQuery<LowBalanceAccount>
{
    public const string Sfql = """
        from readmodel "account-summary"
        | where status = 1 and balance < @below
        | project accountId = id, holder, balance
        | order by balance
        """;
}

public sealed record LowBalanceAccount(AccountId AccountId, string Holder, decimal Balance);
```

Map every registered domain query with one call:

```csharp
builder.Services.AddSourceFlowQueryApi();          // required before mapping (options, limiters, startup check)

app.MapDomainQueries()                             // default prefix "/queries"
   .RequireAuthorization("sourceflow-query");
```

| Method and path | What it does |
|-----------------|--------------|
| `GET /queries` | Lists the domain queries: `name`, `version`, `path`, `parameters` (names), `columns` (result columns). |
| `GET /queries/{name}/v{version}` | Runs one. Each parameter comes from the query string by its name (`?below=100`). `pageSize` and `continuation` are optional query-string values. |

```bash
curl -H "Authorization: Bearer $TOKEN" 'http://localhost:8080/queries/accounts.low-balance/v1?below=100'
```

The response has the same shape as an ad-hoc query (see [Response formats](#response-formats)). An unknown name or
version is `404`.

How permissions apply to a domain query: the query is a published contract, so it runs with the read permissions
(`events`, `readmodels`, `aggregates`, `schema`) whatever the caller holds. Personal data is still shown in clear text
only when the caller holds `sourceflow.query.pii`. Access to the endpoint itself is decided by the authorization policy
you attach to the group. Rate and concurrency limits apply as for the query API.

In process, run the same contract with `IQueryService.ExecuteAsync(new LowBalanceAccounts(Below: 100m))`.

## The per-service query API

Every service can expose a read-only query endpoint over its own events, read models and aggregates, in the pipeline
syntax (SFQL) or a SQL `SELECT` subset ([ADR-026](../ADRs/ADR-026-per-service-query-api-with-sql-syntax-and-injection-proof-compilation.md)).
The language itself, its consistency rules and its injection defences are described in
[Architecture/08 Query side and SFQL](Architecture/08-Query-Side-and-SFQL.md).

### Registering and mapping

```csharp
builder.Services.AddAuthentication().AddJwtBearer();   // any scheme; JwtBearer needs Microsoft.AspNetCore.Authentication.JwtBearer
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("sourceflow-query", p => p.RequireAuthenticatedUser());

builder.Services.AddSourceFlowQueryApi(o =>
{
    o.RequestsPerMinute = 120;
    o.ConcurrencyPerPrincipal = 4;
});

WebApplication app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapSourceFlowQueryApi("/query").RequireAuthorization("sourceflow-query");
app.MapDomainQueries("/queries").RequireAuthorization("sourceflow-query");
```

`AddSourceFlowQueryApi` registers the options, the rate and concurrency limiters, and a startup filter.
`MapSourceFlowQueryApi` and `MapDomainQueries` throw `InvalidOperationException` ("Call
services.AddSourceFlowQueryApi() before mapping the query API.") if it was not called. Both return a
`RouteGroupBuilder`, so you attach the authorization policy, CORS or other conventions to the group.

### Fail-closed startup (SFQ0504)

Every endpoint that `MapSourceFlowQueryApi` or `MapDomainQueries` maps is marked with
`SourceFlowQueryApiEndpointMetadata`. When the request pipeline is built, the startup filter looks for marked endpoints
that have neither an `IAuthorizeData` (`RequireAuthorization(...)`) nor an `IAllowAnonymous` (`AllowAnonymous()`). If
there are any, startup fails:

```text
System.InvalidOperationException: SFQ0504: the SourceFlow query API is mapped without an authorization policy
(SourceFlowQuery, SourceFlowQueryValidate, ...). Add .RequireAuthorization("policy") to the group (or an explicit
.AllowAnonymous() for local demos).
```

There is no way to expose the query API by accident. A demo opts out explicitly, as the Bank sample does when
`Demo:QueryApi` is `true`:

```csharp
app.MapSourceFlowQueryApi().AllowAnonymous();
app.MapDomainQueries().AllowAnonymous();
```

### Endpoints

| Method and path | Permission | What it does |
|-----------------|------------|--------------|
| `POST /query` | `sourceflow.query.adhoc` plus the permission of each source | Runs an ad-hoc query and returns one page. |
| `POST /query/validate` | `sourceflow.query.validate` | Parses, binds and policy-checks the query without running it. Returns `{"valid", "diagnostics", "columns"}`. |
| `POST /query/explain` | `sourceflow.query.explain` | Returns the bound plan without running it: `columns`, `consistency`, `engine` (`postgresql`, `sqlserver`, or `in-memory` when the interpreter runs it, with no statement), `statement` (the parameterised SQL, placeholders only), `parameterCount`, `queryHash`. |
| `GET /query/schema` | `sourceflow.query.schema` | The service's queryable sources: `fingerprint`, `events` (each type with its versions and fields), `readModels` (name, collection, fields) and `aggregates`. Each field is flagged `queryable` and `personalData`. |

The source permissions are `sourceflow.query.events` (for `events` and `stream`), `sourceflow.query.readmodels`,
`sourceflow.query.aggregates` and `sourceflow.query.schema`. `sourceflow.query.pii` shows personal data in clear text,
`sourceflow.query.scan` allows `allow_scan`, and `sourceflow.query.cross-tenant` allows `tenants = 'all'`. The
constants are on `SourceFlow.Query.QueryPermission`.

### Request body

```http
POST /query
Content-Type: application/json

{
  "language": "sql",
  "query": "SELECT id, holder, balance FROM readmodel.\"account-summary\" WHERE balance < @below ORDER BY balance LIMIT 50",
  "parameters": { "below": 100 },
  "pageSize": 50,
  "continuation": null
}
```

| Member | Required | Meaning |
|--------|----------|---------|
| `query` | yes | The query text. |
| `language` | no | `"sql"` for the SQL syntax; anything else (or absent) is the pipeline syntax. |
| `parameters` | no | An object of parameter values, by name without `@`. Each value is converted to the type the binder expects. |
| `pageSize` | no | Rows per page (capped by `QueryLimits.MaxRows`). |
| `continuation` | no | The `continuation` of the previous page. |

Consistency, timeouts and other per-query options are written in the query text (`options (...)` in the pipeline
syntax).

### Response formats

The `Accept` header picks the format.

**JSON** (default, `application/json`):

```json
{
  "columns": [ { "name": "id", "type": "string" }, { "name": "holder", "type": "string" }, { "name": "balance", "type": "decimal" } ],
  "rows": [ [ "0193a7a0-...", "Ada", "20" ] ],
  "continuation": null,
  "consistency": { "sources": [ { "source": "readmodel.account-summary", "level": "eventual", "stale": false } ] },
  "queryHash": "sha256:..."
}
```

Decimals and timestamps are written as strings: decimals exactly, timestamps as RFC 3339 UTC. Integers and booleans are
JSON numbers and booleans, tags are arrays, and JSON values are written as they are.

**NDJSON** (`Accept: application/x-ndjson`): one JSON object per row, keyed by column name, then a trailer line with
`continuation`, `consistency` and `queryHash`.

**CSV** (`Accept: text/csv`): a header row and one line per row (RFC 4180 quoting). A cell that starts with `=`, `+`,
`-`, `@`, TAB or CR is prefixed with `'` so spreadsheets do not run it as a formula. The continuation token, if any, is
in the `SourceFlow-Continuation` response header.

Every query API response carries `X-Content-Type-Options: nosniff`,
`Content-Security-Policy: default-src 'none'; frame-ancestors 'none'`, `Cache-Control: no-store` and
`Cross-Origin-Resource-Policy: same-origin`.

### Errors

Errors are RFC 9457 `application/problem+json`. They carry a code and diagnostics with positions, and never echo the
caller's query or parameter values.

```json
{
  "type": "https://sourceflow.net/problems/query-invalid",
  "title": "The query is not valid.",
  "status": 400,
  "code": "SFQ0102",
  "diagnostics": [ { "code": "SFQ0102", "message": "...", "line": 1, "column": 1, "length": 6 } ]
}
```

| Status | When | `type` suffix |
|--------|------|---------------|
| `400` | Syntax, bind or policy diagnostics (SFQ01xx to SFQ04xx), or a body that is not a JSON object with a `query` string, or JSON nested deeper than `MaxJsonDepth` | `query-invalid` |
| `401` | Not authenticated (from your authentication scheme) | |
| `403` | The caller lacks the endpoint's permission or a source permission (SFQ0501) | `query-forbidden` |
| `404` | Unknown domain query name or version | `query-invalid` |
| `409` | The continuation token does not belong to this query or schema (SFQ0601) | `query-invalid` |
| `413` | Body larger than `MaxBodyBytes` (SFQ0103) | `query-invalid` |
| `415` | Content type other than `application/json` | `query-invalid` |
| `429` | Rate limit (SFQ0502) or per-principal concurrency limit (SFQ0503), with `Retry-After` | `query-throttled` |
| `503` | Service-wide concurrency limit (SFQ0503), with `Retry-After` | `query-throttled` |
| `504` | Statement timeout (`code` = `QueryTimeout`) or an `at_least` consistency wait that timed out (SFQ0602) | `query-timeout` |

### Callers, permissions and tenants

By default `SourceFlowQueryApiOptions.Permissions` reads the caller's permissions from the claims `permission`,
`permissions`, `scope` and `scp`. Space-separated scope lists are split, and only values that start with
`sourceflow.query.` count. So a token with `scope: "sourceflow.query.adhoc sourceflow.query.readmodels"` may run ad-hoc
read-model queries.

The tenant comes from the `sourceflow:tenant` claim, then the `tenant` claim. When neither is present, the ambient
tenant is used (`ITenantResolver`, or `SourceFlowOptions.DefaultTenant`).

Replace either mapping when your identity provider uses other claims:

```csharp
using SourceFlow.Query;

builder.Services.AddSourceFlowQueryApi(o =>
{
    o.Permissions = principal => principal.IsInRole("operators")
        ? QueryPermissions.Of(QueryPermission.Adhoc, QueryPermission.ReadModels, QueryPermission.Events,
                              QueryPermission.Validate, QueryPermission.Explain, QueryPermission.Schema)
        : QueryPermissions.Of(QueryPermission.ReadModels);
    o.Tenant = http => http.User.FindFirst("org_id")?.Value;
});
```

Rate and concurrency limits are partitioned by principal: `Identity.Name`, then the `NameIdentifier` claim, then
`sub`, and `anonymous` when none is present.

### OpenAPI

The endpoints carry names (`SourceFlowQuery`, `SourceFlowQueryValidate`, `SourceFlowQueryExplain`,
`SourceFlowQuerySchema`, `SourceFlowDomainQueries`, `SourceFlowDomainQuery`), summaries and tags. Because they are
mapped as request delegates, ASP.NET Core's OpenAPI generator does not describe their bodies. A hand-written OpenAPI
document for them is in [`deploy/dast/query-api.openapi.json`](../../../deploy/dast/query-api.openapi.json), and a test
fails when its paths differ from the routes the package maps.

## Health checks

The health checks live in the core package (`SourceFlow.Hosting`), and a web host maps them with ASP.NET Core's health
check middleware:

```csharp
builder.Services.AddHealthChecks().AddSourceFlow();          // prefix "sourceflow" by default

app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });     // liveness: no dependencies
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains(SourceFlowHealthChecks.ReadyTag) });
app.MapHealthChecks("/health/sourceflow", new HealthCheckOptions { Predicate = c => c.Tags.Contains(SourceFlowHealthChecks.Tag) });
```

| Check | Tags | Healthy / degraded / unhealthy |
|-------|------|--------------------------------|
| `sourceflow.ready` | `sourceflow`, `ready` | Startup done (validation, provisioning, domain query binding) and receivers started. |
| `sourceflow.store` | `sourceflow`, `ready` | Unhealthy when unreachable or the schema version does not match; degraded above `Health.StoreLatencyDegraded` (250 ms). |
| `sourceflow.transport` | `sourceflow`, `ready` | Unhealthy when the broker is unreachable; degraded when it throttles. Cached for `Health.TransportCacheDuration` (30 s). |
| `sourceflow.outbox` | `sourceflow` | Oldest due message: degraded from 10 s, unhealthy from 5 min. |
| `sourceflow.subscriptions` | `sourceflow` | Degraded when a partition lags more than 30 s, is stalled, or the horizon is held more than 30 s; unhealthy when a partition has been stalled for 5 min. |
| `sourceflow.leases` | `sourceflow` | Degraded when the node has owned less than its fair share of subscription leases for 2 heartbeat intervals. |
| `sourceflow.keys` | `sourceflow` | Unhealthy when payload protection is on and the key service does not issue data keys. Healthy when protection is off. |

Use only the `ready` tag for readiness, and keep liveness free of dependency checks, so that a database or broker
outage takes instances out of the load balancer instead of restarting them. The thresholds are in
`SourceFlowOptions.Health`; [Observability and performance](Observability-and-Performance.md#health-checks) lists them
all. Pass a different prefix with `AddSourceFlow("orders")` if one process registers checks for more than one thing.

## The CLI inside a web host

`dotnet sourceflow` has two kinds of command ([ADR-031](../ADRs/ADR-031-cli-runs-store-commands-globally-and-model-commands-in-the-service.md)).
Store commands run from the global tool. Model commands (`query`, `schema`, `topology`, `db index sync`, `manifest
export` without `--project`) need the service's own model, so they run inside the service. To support them, reference
`SourceFlow.Tool` from the host and call `SourceFlowCli.TryRunAsync` after `Build()` and before mapping endpoints:

```csharp
WebApplication app = builder.Build();

if (await SourceFlow.Tool.SourceFlowCli.TryRunAsync(args, app.Services) is { } exitCode)
{
    return exitCode;     // the CLI ran: don't start the web server
}
```

`TryRunAsync` returns `null` unless the first argument is `sourceflow`, so normal starts are unaffected. Run a command
through the host:

```bash
dotnet run --project samples/Bank/Bank.Api -- sourceflow query 'from readmodel "account-summary" | take 10'
dotnet run --project samples/OrderFulfilment/OrderFulfilment.Host -- --Service=orders sourceflow workflow list
```

See the [SourceFlow.Tool README](SourceFlow.Tool-README.md) for every command.

## Configuration reference

### `SourceFlowQueryApiOptions`

Set with `services.AddSourceFlowQueryApi(o => ...)`.

| Option | Default | Meaning |
|--------|---------|---------|
| `MaxBodyBytes` | `65536` (64 KB) | Largest request body. Larger is `413`. |
| `MaxJsonDepth` | `16` | Deepest JSON nesting in a request body. Deeper is `400`. |
| `RequestsPerMinute` | `60` | Requests per minute per principal (token bucket, no queue). Over is `429` SFQ0502. |
| `ConcurrencyPerPrincipal` | `4` | Concurrent queries per principal. Over is `429` SFQ0503. |
| `ConcurrencyPerService` | `32` | Concurrent queries for the whole service. Over is `503` SFQ0503. |
| `Permissions` | `SourceFlowQueryApiOptions.DefaultPermissions` | `Func<ClaimsPrincipal, QueryPermissions>`: the caller's query permissions. |
| `Tenant` | `sourceflow:tenant`, then `tenant` claim | `Func<HttpContext, string?>`: the caller's tenant; `null` uses the ambient tenant. |

Query engine limits (text length, tokens, joins, page size, `MaxRows` and so on) are `QueryLimits` inside
`QueryPolicySettings`, which the core registers with `TryAddSingleton`. To change them, register your own instance
before `AddSourceFlow`:

```csharp
using SourceFlow.Query;

builder.Services.AddSingleton(QueryPolicySettings.Default with
{
    Limits = QueryLimits.Default with { PageSize = 200, MaxRows = 2000 },
});
```

The statement timeout is a storage option: `PostgreSqlStorageOptions.QueryTimeout` or
`SqlServerStorageOptions.QueryTimeout` (default 5 s, at most 30 s).

### Header and type names

| Name | Value |
|------|-------|
| `SourceFlowHeaders.IdempotencyKey` | `Idempotency-Key` |
| `SourceFlowHeaders.Consistency` | `SourceFlow-Consistency` |
| `CommandResponse` | `record CommandResponse(string Status, long Version, string? Error)` |
| `SourceFlowQueryApiEndpointMetadata` | Endpoint metadata on every query API and domain query endpoint |

## Operational notes

- **Security.** The query API is not a SQL pass-through. Queries are parsed by SourceFlow's own grammar, bound against
  the service's catalog and compiled to parameterised SQL in a read-only transaction. The Bank sample's query API is
  scanned nightly with OWASP ZAP ([`deploy/dast`](../../../deploy/dast/README.md)).
- **Anonymous access** is only for demos. `Demo:QueryApi` and `Demo:PersonalData` in the Bank sample grant every
  permission to every caller; never copy that into a real service.
- **Authorization order.** Call `UseAuthentication()` and `UseAuthorization()` before the endpoints run, as usual. The
  SFQ0504 check only verifies that a decision is attached, not what the policy says.
- **CORS** is off unless you add it. If you enable it, list origins explicitly.
- **Command endpoints and authorization.** `MapCommand` does not require a policy. Add one with
  `.RequireAuthorization(...)` on each endpoint, or on a `MapGroup` that contains them.
- **Request size for commands** follows Kestrel's limits. The query API's own body limit applies only to query
  endpoints.
- **Logging.** Query problems never contain the query text. If you log request bodies elsewhere, remember that query
  parameters may contain personal data.

## Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| Startup fails with `SFQ0504: the SourceFlow query API is mapped without an authorization policy` | `MapSourceFlowQueryApi()` or `MapDomainQueries()` has no `RequireAuthorization` or `AllowAnonymous` | Add `.RequireAuthorization("policy")` to the returned group. |
| `InvalidOperationException: Call services.AddSourceFlowQueryApi() before mapping the query API.` | The options were not registered | Call `builder.Services.AddSourceFlowQueryApi()` before `Build()`. |
| Every query returns `403` with `SFQ0501` | The caller's claims carry no `sourceflow.query.*` values, or they are in a claim the default mapping does not read | Check the token's `scope`/`scp`/`permission(s)` claims, or set `o.Permissions`. |
| `/query` returns `415` | The request has no `Content-Type: application/json` | Send JSON with that content type. |
| A command endpoint returns `400 sourceflow.invalid_request` | The body is not a JSON object, a member has the wrong type, or `If-Match` is not a version | Send an object; quote the version ETag (`"3"`). |
| A route id is ignored | The body already contains that member, and the body wins | Remove it from the body, or keep the two consistent. |
| A command endpoint returns `202` with no body | The command's owner is another service, so it was dispatched | Expected. Query the owner, or wait for its events. |
| A command endpoint returns `409` under load | Concurrency retries were exhausted on a hot stream | See hot streams in [Observability and performance](Observability-and-Performance.md#tuning). |
| `429` with `Retry-After` | The principal exceeded `RequestsPerMinute` or `ConcurrencyPerPrincipal` | Raise the limits, or have clients page and back off. |
| `409` on a continuation | The query text or the service's schema changed between pages | Start again from the first page. |
| `504` with `SFQ0602` | `at_least(@token)` waited longer than its `timeout` | Check projection lag, or use `on_timeout = 'stale'`. |
| `/health/ready` is unhealthy right after deployment | Startup is still provisioning, or the store schema is not migrated | Check `sourceflow.ready` and `sourceflow.store` on `/health/sourceflow`. |

## Links

- [SourceFlow.Net README](SourceFlow.Net-README.md): the core, `AddSourceFlow`, commands and options.
- [Architecture/08 Query side and SFQL](Architecture/08-Query-Side-and-SFQL.md): the language and its guarantees.
- [SourceFlow.Tool README](SourceFlow.Tool-README.md): the `dotnet sourceflow` CLI.
- [Observability and performance](Observability-and-Performance.md): health checks, metrics, dashboards.
- [Security and compliance guide](Security-and-Compliance-Guide.md).
- Design: [doc 14 §9](../Design/14-Query-Side-and-SFQL.md#9-query-api-one-per-service-and-other-surfaces),
  [doc 03](../Design/03-Domain-Programming-Model.md), [doc 08 §5](../Design/08-Observability.md#5-health-checks).
- ADRs: [ADR-018](../ADRs/ADR-018-business-rejections-are-values-not-exceptions.md),
  [ADR-026](../ADRs/ADR-026-per-service-query-api-with-sql-syntax-and-injection-proof-compilation.md),
  [ADR-030](../ADRs/ADR-030-domain-queries-bind-at-startup.md),
  [ADR-031](../ADRs/ADR-031-cli-runs-store-commands-globally-and-model-commands-in-the-service.md).
- Samples: [Bank.Api](../../../samples/Bank/Bank.Api/Program.cs),
  [OrderFulfilment.Host](../../../samples/OrderFulfilment/OrderFulfilment.Host/Program.cs).
