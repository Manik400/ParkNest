# ParkNest

Peer-to-peer parking marketplace. Hosts rent out idle private space; renters book it by the slot.
Settlement runs on a closed-loop prepaid credit ledger, so an overstay bills itself and a renter
cannot refuse to pay — the credits were reserved before the car ever parked.

The full product spec lives in [docs/PRD.md](docs/PRD.md). Working notes, decisions and the
backlog are in [docs/](docs/README.md).

## Status

Phase 0 and Phase 1 are complete in code, and Phase 2 has started. The credit ledger, pricing
rules engine, booking lifecycle, authentication, credit purchase, disputes, payouts, listing
photos and the cancellation policy are all implemented and tested; the Angular admin console and
the Flutter app both run against them.

What remains is not code. Razorpay against the live API needs credentials and an escrow
arrangement; push on a handset needs a Firebase project; the on-call rota needs names. Each is an
account to open or a decision to take, and the code on this side of all three is written and
degrades cleanly without them.

| Area | State |
|---|---|
| Double-entry ledger (hold / release / overstay / settle / payout) | Implemented, 300 tests green |
| Pricing rules engine (city bands, billing increments, overstay multiplier) | Implemented |
| Booking lifecycle + Tier 1 app-confirmed detection | Implemented |
| Listings + PostGIS geo-search | Implemented |
| Auth / identity | Phone + OTP → JWT; every endpoint authorised |
| KYC | Host submits document and photograph; operator verifies; verification is what opens cash-out |
| Sessions | Refresh tokens, rotating, with replay detection; sign-out revokes server-side |
| Rate limiting | Per phone number and per caller, on the OTP endpoints and the payment webhook |
| Availability windows and blackouts | Enforced at booking, per-space time zone |
| Credit purchase (payments) | Built. Sandbox gateway runs locally with no account; Razorpay wired for real money |
| Disputes | Raise with photo evidence, review, uphold or reject; upholding posts a compensating transaction |
| Payouts | Cash-out from the app, gated on KYC and trust score; recording the transfer paid or failed — failure refunds the host |
| Listing photos | Upload, list, delete, in both clients. Local disk by default, behind a storage interface |
| Cancellation policy | Free outside a configurable window; inside it the host is compensated |
| Blocked slots | An over-run into the next renter's slot warns all three parties and makes that booking free to cancel |
| Angular admin/host console | Login, dashboard, listings, bookings, wallet, recharge, disputes, payouts, pricing bands, identity checks |
| Background overstay meter | Bills an over-run while it runs, and notices when it reaches the next renter |
| Tier 2 detection | QR code + geofence, opt-in per space. Host prints the sticker from the app; renters scan it |
| Ratings and trust score | Both parties rate a finished session; the score gates cash-out |
| Wallet concurrency | Row-locked, retried, and tested against real contention |
| Events and notifications | Published in-process by default, RabbitMQ when configured; stored per user |
| Push | FCM v1 off the same stored notification. `Push:Provider=None` by default — no Firebase project needed |
| SignalR | Live session, overstay and wallet updates on a per-user group |
| Metrics | Prometheus at `/metrics`, plus an hourly ledger reconciliation gauge |
| Dashboards and alerts | Provisioned Grafana, Prometheus and Alertmanager in `docker compose`. See [ops/](ops/README.md) |
| Geo-search cache | Optional. `Cache:Provider` of None / Memory / Redis; off by default, invalidated on listing changes |
| Pricing band audit | Every band edit recorded with both sides, the operator and the reason; visible in the console |
| On-call | [ops/on-call.md](ops/on-call.md) — routing, response steps per alert. The rota itself is unfilled |
| Flutter renter + host app | Sign-in, map search from your location, quote, book, session, wallet, vehicles, listing a space, disputes, QR check-in, ratings, notifications |

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
ops/                        Prometheus scrape config, alert rules, provisioned Grafana dashboard.
docs/                       Spec, ADRs, work log, backlog.
```

It is a modular monolith on purpose. The module boundaries match the services in PRD §11, so
peeling one out later is a project split rather than a rewrite.

## Running it

Two processes: the API and the Angular dev server. Postgres has to exist first; Docker is one way
to get it, not a requirement — a local PostGIS-enabled Postgres works just as well, point
`ConnectionStrings:ParkNest` at it.

```bash
docker compose up -d                 # optional: Postgres + PostGIS (Redis and RabbitMQ are both optional - see Configuration)
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
dotnet test tests/ParkNest.UnitTests  # 300 tests, no Docker required
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

Finding parking asks for location, and works without it — permission refused just means results
come from the city centre instead, with a banner saying so. Maps are OpenStreetMap tiles, so no
key or billing account is needed.

Checking in scans the sticker at the space when there is one. The camera is asked for only at
that moment, the location fix that corroborates it is asked for precisely, and if either is
refused the screen falls back to "I have parked" — which is a Tier 1 check-in, recorded honestly
as the renter's word. Hosts get the QR for their own space by tapping it under **Hosting**.

```bash
flutter test                         # 36 tests
flutter analyze
```

## Watching it run

```bash
docker compose up -d prometheus alertmanager alert-sink grafana
```

Grafana at <http://localhost:3000> opens on the dashboard without a login. It scrapes the API on
the host, so start the API first — details and the one number worth alerting on are in
[ops/](ops/README.md).

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

## Sending the OTP

Three providers, and the choice is really about who owns the last hop to the handset.

```jsonc
// src/ParkNest.Api/appsettings.json
"Sms": { "Provider": "Log" }        // prints the code; Development only, Production refuses to start
"Sms": { "Provider": "Gateway", "BaseUrl": "http://192.168.1.20:8080",
         "Username": "...", "Password": "..." }   // a handset you own, with its own SIM
"Sms": { "Provider": "Msg91", "AuthKey": "...", "TemplateId": "..." }   // a DLT-registered aggregator
```

`Gateway` is the free one. It posts to [SMS Gateway for Android](https://github.com/capcom6/android-sms-gateway)
(AGPL-3.0) running on a spare Android phone — the phone sends the message off its own SIM, over
its own allowance, and nobody is billed per message. Any endpoint with the same shape works, a
GSM modem behind Gammu included, because the contract is one `POST /message` and two fields.

It is worth being exact about what that does and does not solve. **There is no free software
package that delivers SMS.** Delivery is a licensed telecom function; every "SMS library" on
npm or NuGet is an HTTP client for somebody's paid account. Owning the last hop is the only way
around that, and it buys you development and a pilot, not production:

- one consumer SIM sends a few hundred messages a day before its carrier treats it as spam;
- Indian regulation puts commercial transactional SMS on DLT-registered headers, which a personal
  SIM does not have and cannot get;
- the gateway is a phone on a desk, so a flat battery is an outage. `SmsGatewayOtpSender` treats
  that as an expected failure and surfaces "try again shortly" rather than a 500.

So `Gateway` is what you develop and pilot against, and `Msg91` is what you launch on. Both
implement the same `IOtpSender`, so the switch is a config change and nothing else moves.

## Configuration

Platform economics are configuration, never constants — see the `Platform` section of
`src/ParkNest.Api/appsettings.json` for commission rate, billing increment, grace period, the
cash-out floor, and the cancellation window and fee. City price bands are database rows managed through `/api/admin/pricing`.

Push notifications are off by default and stay off in Production unless configured. Every message
is stored and pushed down the socket regardless — Firebase only adds the buzz on a handset that is
not connected — so a deployment with no Firebase project loses nothing but the buzz.

```jsonc
// src/ParkNest.Api/appsettings.json
"Push": {
  "Provider": "Firebase",             // "None" stores and sockets, without pushing
  "ProjectId": "parknest-12345",
  "ServiceAccountKeyPath": "/run/secrets/firebase.json"   // never commit the key
}
```

Geo-search can sit behind a cache. It is off by default because one indexed PostGIS query is
already inside its latency budget for a single city, and requiring Redis to run the app would be
paying a multi-city cost years early.

```jsonc
"Cache": { "Provider": "None" }      // every search hits PostGIS
"Cache": { "Provider": "Memory" }    // in-process: the whole benefit on one instance, stale on any other
"Cache": { "Provider": "Redis", "ConnectionString": "localhost:6379" }
```

Development defaults to `Memory`, so the caching path actually runs locally without anybody
installing Redis. Publishing or withdrawing a listing invalidates every cached search; `SearchTtlSeconds`
caps how stale anything can get through a path nothing thought to invalidate. `OriginPrecision` is
how many decimal places of latitude and longitude go into the key — four is about 11 metres, and
without that rounding a phone's GPS jitter mints a new key on every tap and the cache never hits.

The app registers its token now. `PushService` asks for the permission after sign-in rather than
at launch, re-registers on every start because the platform rotates tokens on its own schedule,
and unregisters on sign-out before the session is cleared — so a borrowed handset stops receiving
the previous account's bookings.

What is still missing is a Firebase project. Create one, download `google-services.json` into
`clients/mobile/android/app/`, and push starts working on the next build: the Google Services
Gradle plugin is applied only when that file is present, so a clone without it builds and runs
exactly as before. The file is git-ignored, because one developer's Firebase project silently
becoming everybody's is not a good failure.

## A note on the database

**It must be UTF-8.** Encoding is fixed when a database is created, and a Windows default lands on
WIN1252 — which has no rupee sign, no Devanagari, and no Kannada. The first symptom is a write
failing deep inside a background handler; the real problem is a database that cannot hold a large
share of its users' names. The API reports it at startup rather than at the first insert that
happens to contain one.

```sql
CREATE DATABASE parknest ENCODING 'UTF8' TEMPLATE template0
  LC_COLLATE 'en_US.UTF-8' LC_CTYPE 'en_US.UTF-8';
```

The Docker image in `docker-compose.yml` already does this. A hand-made local database may not.

## A note on compliance

A credit balance that converts back to cash looks a lot like a Prepaid Payment Instrument under
RBI rules. The intended model is an escrow/aggregator partnership with credits as an internal
accounting layer on top — not ParkNest issuing a wallet. See PRD §16 and
[docs/adr/0004-credits-are-not-a-wallet.md](docs/adr/0004-credits-are-not-a-wallet.md). Get real
legal advice before recharge and payout volume scales.
