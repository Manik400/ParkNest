# ParkNest

Peer-to-peer parking marketplace. Hosts rent out idle private space; renters book it by the slot.
Settlement runs on a closed-loop prepaid credit ledger, so an overstay bills itself and a renter
cannot refuse to pay — the credits were reserved before the car ever parked.

The full product spec lives in [docs/PRD.md](docs/PRD.md). Working notes, decisions and the
backlog are in [docs/](docs/README.md).

## Status

Phase 0 backend scaffold. The credit ledger, pricing rules engine and booking lifecycle are
implemented and tested; the Flutter and Angular clients are not started yet.

| Area | State |
|---|---|
| Double-entry ledger (hold / release / overstay / settle / payout) | Implemented, 26 tests green |
| Pricing rules engine (city bands, billing increments, overstay multiplier) | Implemented |
| Booking lifecycle + Tier 1 app-confirmed detection | Implemented |
| Listings + PostGIS geo-search | Implemented |
| Auth / identity | Not started — endpoints are currently unauthenticated |
| RabbitMQ, SignalR, payment aggregator | Not started (Phase 1) |
| Flutter app, Angular admin | Not started |

## Layout

```
src/
  ParkNest.Domain/          Entities and invariants. No dependencies.
  ParkNest.Application/     Use cases: ledger, wallet, pricing, listings, bookings.
  ParkNest.Infrastructure/  EF Core, Postgres/PostGIS, migrations, DI.
  ParkNest.Api/             ASP.NET Core controllers and error mapping.
tests/
  ParkNest.UnitTests/       Service-level tests against in-memory SQLite.
docs/                       Spec, ADRs, work log, backlog.
```

It is a modular monolith on purpose. The module boundaries match the services in PRD §11, so
peeling one out later is a project split rather than a rewrite.

## Running it

```bash
docker compose up -d                 # Postgres + PostGIS, Redis, RabbitMQ
dotnet tool restore                  # pins dotnet-ef 8.0.10
dotnet dotnet-ef database update --project src/ParkNest.Infrastructure --startup-project src/ParkNest.Api
dotnet run --project src/ParkNest.Api
```

Swagger is at `/swagger` in Development; `/health` is always available.

```bash
dotnet test                          # 26 tests, no Docker required
```

Tests run on in-memory SQLite, so they need no container and finish in about a second.

## Configuration

Platform economics are configuration, never constants — see the `Platform` section of
`src/ParkNest.Api/appsettings.json` for commission rate, billing increment, grace period and the
cash-out floor. City price bands are database rows managed through `/api/admin/pricing`.

## A note on compliance

A credit balance that converts back to cash looks a lot like a Prepaid Payment Instrument under
RBI rules. The intended model is an escrow/aggregator partnership with credits as an internal
accounting layer on top — not ParkNest issuing a wallet. See PRD §16 and
[docs/adr/0004-credits-are-not-a-wallet.md](docs/adr/0004-credits-are-not-a-wallet.md). Get real
legal advice before recharge and payout volume scales.
