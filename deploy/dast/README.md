# DAST: OWASP ZAP scan of the query API

The nightly `dast` job (`.github/workflows/v3-nightly.yml`) runs the OWASP ZAP API scan against the TravelBooking
sample's Bookings service, on its per-service query API (doc 14 §9, plan 08). Any medium or high finding fails the night.

| File | What it is |
|------|------------|
| `query-api.openapi.json` | The OpenAPI document that drives the scan. It covers `/query`, `/query/validate`, `/query/explain`, `/query/schema`, `/queries` and `/queries/{name}/v{version}`, with request examples. The endpoints are mapped as request delegates, which ASP.NET Core's OpenAPI generator skips, so this document is written by hand. Test UT-ASP-37 fails when its paths differ from the routes the API maps. |
| `zap-rules.tsv` | Rules ZAP should ignore, each with its reason. |

The injection corpus over HTTP (QRY-57..59) runs with the end-to-end tests, in
`tests/EndToEnd/SourceFlow.EndToEnd.Tests/QueryApiScenarios.cs`. It checks that every payload is rejected with an
`SFQ01xx` or `SFQ02xx` code or runs with its values as parameters. It also checks for zero database errors, no markup
in any response, and no writes to the event, stream or document tables.

## Running it locally

```bash
docker compose up -d --build --wait postgres localstack hotels bookings
mkdir -p zap && cp deploy/dast/* zap/
docker run --rm --network sourceflow-demo_default -v "$PWD/zap:/zap/wrk:rw" ghcr.io/zaproxy/zaproxy:stable \
  zap-api-scan.py -t /zap/wrk/query-api.openapi.json -f openapi -O http://bookings:8080 \
  -c zap-rules.tsv -J zap-report.json -r zap-report.html
```

## Latest result (WP-4.7)

119 rules passed, with no high, medium or low findings. The first run raised one low finding: the query responses had
no `Cross-Origin-Resource-Policy` header. The query API now sends `Cross-Origin-Resource-Policy: same-origin` with its
other security headers.
