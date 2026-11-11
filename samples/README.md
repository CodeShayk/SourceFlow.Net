# SourceFlow.Net v3 demo

The repository root `docker-compose.yml` starts three containers:

- **PostgreSQL**
- **The Bank sample service**
- **The Studio demo console**

```bash
docker compose up -d            # pulls the published images from ghcr.io
docker compose up -d --build    # or builds them from this checkout
docker compose down -v          # stops the demo and deletes its data
```

| What | URL |
|------|-----|
| Studio demo console | http://localhost:5080 |
| Bank service | http://localhost:8080 |
| PostgreSQL (user, password and database: `sourceflow` / `sourceflow` / `bank`) | `localhost:55432` |

`SOURCEFLOW_TAG` selects the image tag. The default is `v3-latest`. You can also use a branch name such as `v3-m2-postgresql`, or a commit tag such as `sha-a5531c8`.

## Images

| Image | Source |
|-------|--------|
| `ghcr.io/codeshayk/sourceflow-bank-api` | [Bank/Bank.Api](Bank/Bank.Api) |
| `ghcr.io/codeshayk/sourceflow-studio-demo` | [Studio/SourceFlow.Studio.Demo](Studio/SourceFlow.Studio.Demo) |

Both images are built for `linux/amd64` and `linux/arm64`. They are published by `.github/workflows/v3-images.yml` on every push to `v3.0.0` or a `v3/*` branch, and the workflow can also be started by hand.

## Bank service (`Bank.Api`)

The Accounts, Transfers and Notifications modules run in one process on PostgreSQL. The schema is created at startup (`SourceFlow__AutoMigrate`). Messages between the modules use the in-memory transport.

| Endpoint | Command |
|----------|---------|
| `POST /accounts/{accountId}` `{ "holder": "Ada", "initialDeposit": 100 }` | Open an account |
| `POST /accounts/{accountId}/deposits` `{ "amount": 50 }` | Deposit |
| `POST /accounts/{accountId}/withdrawals` `{ "amount": 20 }` | Withdraw |
| `POST /accounts/{accountId}/close` `{ "reason": "…" }` | Close the account |
| `POST /transfers/{transferId}` `{ "from": "…", "to": "…", "amount": 25 }` | Transfer, run by the `MoneyTransfer` process manager |

Responses carry:

- an `ETag` with the stream version;
- a `SourceFlow-Consistency` token for read-your-writes queries.

A request can send `Idempotency-Key` and `If-Match` headers.

With `Demo__QueryApi=true`, set in the compose file, the service also maps three query endpoints:

| Endpoint | Purpose |
|----------|---------|
| `POST /query` `{ "query", "language": "sfql"\|"sql", "parameters", "pageSize", "continuation" }` | Run a query |
| `POST /query/validate` | Check a query without running it |
| `GET /query/schema` | Describe the queryable data |

> **These query endpoints are for the demo only.** They have no authentication and give every caller read access to events, read models, aggregates and the schema. Personal data stays redacted unless `Demo__PersonalData=true`. The production query API (authorization, rate limits, OpenAPI) is `MapSourceFlowQueryApi` in `SourceFlow.AspNetCore` (WP-4.2). Don't expose this service publicly.

## Studio demo console

The console is an interim tool for exploring the demo. It is **not** SourceFlow Studio, the workflow designer described in doc 15 (track S). It reaches the service only over HTTP, through a proxy on its own origin that allows a fixed list of paths.

- **Query:** run SFQL or SQL-syntax queries with parameters and page through results. It shows the consistency applied to each source and any diagnostics. Built-in examples cover:
  - joins and grouping;
  - temporal aggregate state;
  - streams;
  - subqueries.
- **Schema:** list the event types (every version), read models and aggregates. Fields are marked `[Queryable]` or personal data. Each item has a one-click starter query.
- **Commands:** open, deposit, withdraw, transfer and close. After each command you can run a read-your-writes query (`consistency = at_least(@token)`), or view the aggregate state and its event stream.
- **Seed demo data:** create several accounts with activity and transfers.
- **Activity:** list the commands sent from this browser, with their versions and consistency tokens.
