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

## OrderFulfilment

[OrderFulfilment](OrderFulfilment) is a commerce domain split across four services and the workflow that takes an order
from placement to delivery:

- **Orders** takes orders and owns the **order-fulfilment** workflow;
- **Inventory** reserves stock, **Payments** charges and refunds, **Shipping** ships.

The workflow reserves the stock, takes the payment, waits for a reviewer when the order is large, then ships. A rejection
or a timeout undoes what was done in reverse order (the payment refunded, the stock released); after its point of no
return, an address the carrier can't deliver to waits for the customer's correction instead.

```bash
docker compose -f samples/OrderFulfilment/docker-compose.yml up -d --build     # from the repository root
```

| What | URL |
|------|-----|
| Orders service | http://localhost:8090 |
| Inventory, Payments, Shipping services | http://localhost:8091, 8092, 8093 |
| SourceFlow Studio (read-only) | http://localhost:5090 |
| Aspire dashboard | http://localhost:18890 |

The ports differ from TravelBooking's, so both can run at once. The [sample's README](OrderFulfilment/README.md) walks
through it.

## Images

| Image | Source |
|-------|--------|
| `ghcr.io/codeshayk/sourceflow-travel-bookings` | [TravelBooking/Dockerfile](TravelBooking/Dockerfile) (`SERVICE=Bookings`) |
| `ghcr.io/codeshayk/sourceflow-travel-hotels` | [TravelBooking/Dockerfile](TravelBooking/Dockerfile) (`SERVICE=Hotels`) |
| `ghcr.io/codeshayk/sourceflow-travel-studio` | [TravelBooking/Studio.Dockerfile](TravelBooking/Studio.Dockerfile) |
| `ghcr.io/codeshayk/sourceflow-order-fulfilment` | [OrderFulfilment/Dockerfile](OrderFulfilment/Dockerfile) (each container sets `Service`) |
| `ghcr.io/codeshayk/sourceflow-order-studio` | [OrderFulfilment/Studio.Dockerfile](OrderFulfilment/Studio.Dockerfile) |

They are built for `linux/amd64` and `linux/arm64` and published by `.github/workflows/v3-images.yml` on every push to
`v3.0.0`. `SOURCEFLOW_TAG` selects the tag the compose files pull (default `v3-latest`).

The test suites also run against a fixture domain in [tests/Fixtures](../tests/Fixtures), a Bank domain. It is test data,
not an example.
