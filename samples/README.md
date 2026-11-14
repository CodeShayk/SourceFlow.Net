# SourceFlow.Net v3 samples

## TravelBooking

[TravelBooking](TravelBooking) is a domain split across two services and the workflow between them:

- **Bookings** takes bookings from guests and owns the **trip-booking** workflow;
- **Hotels** owns hotels and their rooms.

The workflow holds a room at the hotel, waits for the guest to confirm, then confirms the reservation. It retries,
times out, releases the hold when a later step fails, and stops for an operator after its point of no return.
SourceFlow Studio draws, validates, simulates and tests it.

```bash
docker compose up -d --build     # from the repository root
```

| What | URL |
|------|-----|
| Bookings service | http://localhost:8080 |
| Hotels service | http://localhost:8081 |
| SourceFlow Studio (read-only) | http://localhost:5080 |
| Aspire dashboard | http://localhost:18888 |

The [sample's README](TravelBooking/README.md) walks through it.

## Images

| Image | Source |
|-------|--------|
| `ghcr.io/codeshayk/sourceflow-travel-bookings` | [TravelBooking/Dockerfile](TravelBooking/Dockerfile) (`SERVICE=Bookings`) |
| `ghcr.io/codeshayk/sourceflow-travel-hotels` | [TravelBooking/Dockerfile](TravelBooking/Dockerfile) (`SERVICE=Hotels`) |
| `ghcr.io/codeshayk/sourceflow-travel-studio` | [TravelBooking/Studio.Dockerfile](TravelBooking/Studio.Dockerfile) |

They are built for `linux/amd64` and `linux/arm64` and published by `.github/workflows/v3-images.yml` on every push to
`v3.0.0`. `SOURCEFLOW_TAG` selects the tag `docker compose up` pulls (default `v3-latest`).

The test suites also run against two fixture domains, in [tests/Fixtures](../tests/Fixtures): a Bank domain and the
OrderFulfilment services. They are test data, not examples.
