# ParkNest

Peer-to-peer parking marketplace. Hosts rent out idle private space; renters book it by the slot.
Settlement runs on a closed-loop prepaid credit ledger, so an overstay bills itself and a renter
cannot refuse to pay — the credits were reserved before the car ever parked.

The full product spec lives in [docs/PRD.md](docs/PRD.md). Working notes, decisions and the
backlog are in [docs/](docs/README.md).

## Status

Phase 0. The credit ledger, pricing rules engine, booking lifecycle, authentication, the credit
purchase flow, disputes and payouts are implemented and tested; the Angular admin console and the
Flutter app both run against them. What is left is device location and a map in the app, and
Razorpay against the live API — which needs credentials and an escrow arrangement rather than
more code.

| Area | State |
|---|---|
| Double-entry ledger (hold / release / overstay / settle / payout) | Implemented, 169 tests green |
| Pricing rules engine (city bands, billing increments, overstay multiplier) | Implemented |
| Booking lifecycle + Tier 1 app-confirmed detection | Implemented |
| Listings + PostGIS geo-search | Implemented |
| Auth / identity | Phone + OTP → JWT; every endpoint authorised |
| Sessions | Refresh tokens, rotating, with replay detection; sign-out revokes server-side |
| Rate limiting | Per phone number and per caller, on the OTP endpoints and the payment webhook |
| Availability windows and blackouts | Enforced at booking, per-space time zone |
| Credit purchase (payments) | Built. Sandbox gateway runs locally with no account; Razorpay wired for real money |
| Disputes | Raise, review, uphold or reject; upholding posts a compensating transaction |
| Payouts | Cash-out, plus recording the transfer paid or failed — failure refunds the host |
| Angular admin/host console | Login, dashboard, listings, bookings, wallet, recharge, disputes, payouts, pricing bands |
| RabbitMQ, SignalR | Not started (Phase 1) |
| Flutter renter + host app | Sign-in, search, quote, book, session, wallet, vehicles, hosting, disputes |

## Layout

```
src/
  ParkNest.Domain/          Entities and invariants. No dependencies.
  ParkNest.Application/     Use cases: ledger, wallet, pricing, listings, bookings.
  ParkNest.Infrastructure/  EF Core, Postgres/PostGIS, migrations, DI.
  ParkNest.Api/             ASP.NET Core controllers and error mapping.
tests/
  ParkNest.UnitTests/       Service-level tests against in-memory SQLite.
  ParkNest.IntegrationTests/ Geo-search against a real PostGIS. Skips when there is no database.
clients/
  admin/                    Angular 18 admin and host console.
  mobile/                   Flutter renter and host app.
docs/                       Spec, ADRs, work log, backlog.
```

It is a modular monolith on purpose. The module boundaries match the services in PRD §11, so
peeling one out later is a project split rather than a rewrite.

## Running it

Two processes: the API and the Angular dev server. Postgres has to exist first; Docker is one way
to get it, not a requirement — a local PostGIS-enabled Postgres works just as well, point
`ConnectionStrings:ParkNest` at it.

```bash
docker compose up -d                 # optional: Postgres + PostGIS (Redis and RabbitMQ are unused until Phase 1)
dotnet tool restore                  # pins dotnet-ef 8.0.10
dotnet dotnet-ef database update --project src/ParkNest.Infrastructure --startup-project src/ParkNest.Api
dotnet run --project src/ParkNest.Api --launch-profile https
```

**Use the `https` profile.** It binds both `https://localhost:7139` and `http://localhost:5109`;
the `http` profile binds only the latter, and the admin console is configured to call 7139
(`clients/admin/src/environments/environment.ts`), so every request from the browser fails with
nothing in the API log to explain it.

Swagger is at `/swagger` in Development; `/health` is always available.

```bash
dotnet test tests/ParkNest.UnitTests  # 169 tests, no Docker required
```

Tests run on in-memory SQLite, so they need no container and finish in about a second.

The integration suite covers geo-search, which SQLite fundamentally cannot — the generated
`geography` column, the GiST index and the raw SQL only exist in Postgres. It skips unless pointed
at a database:

```bash
PARKNEST_TEST_CONNECTION="Host=localhost;Port=5432;Database=parknest;Username=parknest;Password=parknest"   dotnet test tests/ParkNest.IntegrationTests
```

A plain `dotnet test` runs both; without that variable the integration tests report as skipped
rather than passing silently.

Any PostGIS-enabled Postgres will do — point the variable at a database you do not mind the tests
writing to. They clean up after themselves, but a scratch database is still the right habit.

### Admin console

```bash
cd clients/admin
npm install
npm start                            # ng serve, http://localhost:4200
```

There is no proxy. The app calls the API cross-origin at the URL in
`src/environments/environment.ts`, and the API allows `localhost:4200` via a CORS policy that is
registered only outside Production — so `npm start` is the whole story, and the first request will
fail unless you have accepted the API's self-signed certificate. Visit
<https://localhost:7139/swagger> once and click through the browser warning.

Needs the API running. Sign in with any phone number — outside Production the API returns the
one-time code in the response and the login screen displays it, because `Sms:Provider` defaults to
`Log`. The first sign-in creates the account.

Codes are rate limited: one a minute per number, five an hour. If you are testing sign-in in a
loop, use a different number rather than waiting, or raise `Auth:OtpMaxRequestsPerWindow`.

To reach the pricing screen you need the admin role: add your number to `Auth:AdminPhones` in
`src/ParkNest.Api/appsettings.json` before first sign-in.

### Mobile app

```bash
cd clients/mobile
flutter pub get
flutter run                          # pick a device; Chrome works without an emulator
```

Needs the API running on the **http** profile (`http://localhost:5109`), not https — a mobile
client correctly refuses the self-signed certificate, and teaching it not to is a habit that
escapes into release builds. Debug builds carry a network security config scoped to loopback so
cleartext works there and nowhere else.

The base URL defaults per platform, because `localhost` inside an Android emulator is the emulator.
For a real handset, point it at your machine:

```bash
flutter run --dart-define=PARKNEST_API_BASE_URL=http://192.168.1.20:5109
```

Sign in with any number; outside Production the code comes back in the response and the screen
fills it in.

```bash
flutter test                         # 15 tests
flutter analyze
```

## Buying credits

Development runs on the built-in **sandbox gateway** — no merchant account, no credentials, no
network. Press **Add credits** on the wallet page and the browser goes to a local checkout sheet
with *Pay* and *Simulate a declined payment*; either posts an HMAC-signed callback to the real
webhook endpoint, which verifies it exactly as it would a live one. No money moves.

```jsonc
// src/ParkNest.Api/appsettings.json
"Payments": { "Provider": "Sandbox" }   // local, free, refused in Production
"Payments": { "Provider": "Razorpay", "KeyId": "…", "KeySecret": "…", "WebhookSecret": "…" }
"Payments": { "Provider": "None" }      // payment endpoints report "not configured"
```

There is no open-source gateway that settles real funds — that needs a licensed PSP — so the
sandbox reproduces the *protocol* rather than pretending to be one, and switching to Razorpay
changes nothing but the edge of the system. See
[docs/adr/0006-sandbox-payment-gateway.md](docs/adr/0006-sandbox-payment-gateway.md).

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
