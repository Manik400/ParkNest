# ParkNest — System Overview

*What we're solving, how we're solving it, and what actually exists in the repo today.*

Last verified against the codebase on **2026-08-19** (branch `feat/hardening-and-completion`,
169 tests green).

This is the orientation document. Deeper detail lives in:

- [PRD.md](PRD.md) — the full product spec
- [ledger-model.md](ledger-model.md) — the money rules, in detail
- [architecture.md](architecture.md) — layering and what is deliberately not built yet
- [adr/](adr/) — the five decisions we don't want to re-litigate

---

## 1. The problem

Two failures sit on top of each other.

**A supply gap.** Millions of private spaces — driveways, empty plots, society and office
basements — sit idle most of the day, while drivers circle the block. Existing parking apps
aggregate *commercial* lots. Nobody is unlocking the private inventory. This is the hotel-vs-Airbnb
gap, applied to parking.

**A trust gap that prevents the informal market from working.** Anyone can already say "park in my
driveway, pay me when you leave." It breaks down two ways, every time:

1. **Duration disputes.** No objective record of time used. "You said one hour" becomes an argument
   in a driveway.
2. **Payment refusal.** Once the car has left, the host has zero leverage. Collection depends
   entirely on the renter's goodwill.

The second one is the killer. It is why this market has never formalised on its own: a host who
gets stiffed once stops listing. Any solution that leaves money to be *collected* after the fact
inherits the same failure.

---

## 2. The solution

**Money is never collected. It is only released from a balance that already exists.**

A renter must hold prepaid credits before they can book. At booking time the system places a
**hold** on credits equal to duration × rate — reserved, not yet the host's. When the session ends,
the actual duration is measured and the hold settles: unused credits go back, used credits go to
the host minus commission.

This dissolves both failure modes structurally rather than procedurally:

- An overstay bills itself, at a premium multiplier, from credits already on the platform.
- There is no invoice to refuse, because there was never an invoice — only a balance that unlocks.

Real money crosses the platform boundary exactly twice: **recharge in**, **host cash-out**. Every
movement in between is an internal ledger entry.

### The case we designed for

A renter overstays and cannot cover the excess. We take whatever credits are there, record the
remainder as a `ShortfallAmount`, mark the booking `InViolation`, and dock the trust score by 10.

The host still receives every credit that could actually be collected. The platform restricts the
renter's future access instead of chasing cash from someone who has already driven away. That
tradeoff — *degrade the debtor's access, don't pursue the debt* — is the whole design.

### Compliance caveat

A credit balance that converts back to cash resembles a Prepaid Payment Instrument under RBI rules.
The intended model is an escrow/aggregator partnership with credits as an internal accounting layer
on top — **not** ParkNest issuing a wallet. See [adr/0004-credits-are-not-a-wallet.md](adr/0004-credits-are-not-a-wallet.md)
and PRD §16. Get real legal advice before recharge and payout volume scales.

---

## 3. What we have built

Phase 0 is functionally complete: ledger, pricing, booking lifecycle, listings with geo-search,
authentication, availability enforcement, vehicles, credit purchase, disputes, payouts, and an
Angular admin console running against all of it.

| Area | State |
|---|---|
| Double-entry ledger (hold / release / overstay / settle / payout) | Built, invariants enforced in code *and* in Postgres |
| Pricing rules engine (city bands, billing increments, overstay multiplier) | Built |
| Booking lifecycle + Tier 1 app-confirmed detection | Built |
| Listings + PostGIS geo-search | Built |
| Auth / identity (phone + OTP → JWT) | Built, every endpoint authorised, deny-by-default |
| Sessions | Refresh tokens with rotation and replay detection; signing out revokes server-side |
| Rate limiting | Per phone number in the service, per caller at the edge, on OTP and the webhook |
| Availability windows and blackouts | Built, enforced at quote and at booking, per-space time zone |
| Vehicles | Built |
| Payments / credit purchase | Built. Sandbox gateway runs the whole flow locally; Razorpay wired for real money |
| Disputes | Built. Raise, review, uphold or reject; upholding posts a compensating transaction |
| Payouts | Built. Cash-out request, and recording the transfer as paid or failed with a refund |
| Angular admin/host console | Login, dashboard, listings, bookings, wallet, disputes, payouts, pricing bands |
| RabbitMQ, SignalR, background overstay meter | Not started (Phase 1) |
| Flutter renter app | Not started |

**169 unit tests, all green, ~4s.** They run on in-memory SQLite, so CI needs no container.

A second suite covers geo-search against a real PostGIS. It skips unless `PARKNEST_TEST_CONNECTION`
is set, and CI sets it in the job that already runs a PostGIS container — the geo path is the one
thing SQLite fundamentally cannot exercise.

### Structure

```
src/
  ParkNest.Domain/          Entities and invariants. Zero dependencies.
  ParkNest.Application/     Use cases: wallets, bookings, pricing, listings, auth, payments, vehicles.
  ParkNest.Infrastructure/  EF Core, Postgres/PostGIS, migrations, JWT, SMS, gateway, DI.
  ParkNest.Api/             Controllers and error→HTTP mapping. No business logic.
tests/
  ParkNest.UnitTests/       Service-level tests on in-memory SQLite.
  ParkNest.IntegrationTests/ Geo-search against a real PostGIS. Skips without a database.
clients/
  admin/                    Angular 18 admin and host console.
```

Dependencies point one way: `Api → Infrastructure → Application → Domain`. `Application` reaches
persistence only through `IParkNestDbContext`, which is precisely why the test suite can swap
Postgres for SQLite wholesale.

It is a **modular monolith on purpose** ([adr/0001](adr/0001-modular-monolith.md)). The module
boundaries match the services in PRD §11, so extracting one later is a project split, not a
rewrite.

### The one rule that shapes the code

**Only the wallet module may mutate a balance.** Booking decides *timing*; wallet decides *money*.
This is why [BookingService.cs](../src/ParkNest.Application/Bookings/BookingService.cs) contains no
arithmetic on wallet columns anywhere — it calls `PlaceHold`, `ReleaseHold`, `DebitOverstay`,
`Settle` and nothing else. Every credit movement lands in one auditable place.

### A session, end to end

```
POST /api/bookings              → quote from city band → PlaceHold → Booking(Held)
POST /api/bookings/{id}/start   → Booking(Active), meter starts
POST /api/bookings/{id}/end     → measure actual duration from the BOOKED start
                                   ├ under  → ReleaseHold(unused) → Settle(used)
                                   └ over   → DebitOverstay(excess) → Settle(hold + covered)
                                                └ uncovered → Booking(InViolation), trust −10
```

Billing runs from the **booked** start, not the check-in. Arriving late doesn't shorten the window
the host held open, and the space was unavailable to anyone else regardless.

### Buying credits

Money entering the system is the other half of §2's "real money crosses the boundary twice". The
flow, whichever gateway is selected:

```
POST /api/payments/orders     → PaymentOrder(Created), amount fixed server-side
                                → browser goes to the gateway's checkout
gateway → POST /api/payments/webhook   signed; verified before the body is parsed
                                → Recharge ledger transaction, key "payment:{orderId}"
                                → PaymentOrder(Paid)
GET  /api/payments/orders/{id} → client polls; this is how it learns the money arrived
```

Three properties do the work, and they are the same for the sandbox and for Razorpay:

- **The amount comes from our own order record, never the callback.** A tampered-but-signed body
  claiming a larger figure has nothing to stand on.
- **The idempotency key is derived from the order id**, so a gateway retrying an unacknowledged
  callback cannot credit twice. Verified live: three deliveries, one `Recharge` transaction.
- **Only `payment.captured` counts.** `payment.authorized` means funds are merely held and can
  still fall through.

**Three providers**, selected by `Payments:Provider`:

| Provider | What it is |
|---|---|
| `Sandbox` | Runs in-process. Serves its own checkout page, signs its own HMAC-SHA256 callbacks, emits Razorpay's event envelope. No account, no network, no money. **Refused at startup in Production.** |
| `Razorpay` | Real money. Needs `KeyId`, `KeySecret`, `WebhookSecret`. |
| `None` | Payment endpoints return a clear "not configured"; everything else still works. |

The sandbox exists because there is no open-source gateway that settles real funds — that requires
a licensed PSP — and because Razorpay test mode needs a signup and an ngrok tunnel to receive
webhooks on a laptop, so it cannot run in CI. See
[adr/0006-sandbox-payment-gateway.md](adr/0006-sandbox-payment-gateway.md).

It signs the callbacks it verifies, which makes it a credit-minting oracle for anyone who can open
its checkout page. That is what a gateway with no money behind it necessarily is, and it is why
`PaymentOptions.IsConfigured` deliberately excludes it and startup refuses it in Production.

### API surface

33 endpoints across 8 controllers. `/health`, the two auth endpoints, the payment webhook and the
sandbox checkout page are anonymous; everything else requires a token, because `Program.cs` sets a
`FallbackPolicy` requiring an authenticated user — forgetting an `[Authorize]` attribute fails
closed rather than exposing an endpoint.

```
POST   /api/auth/request-otp                 anonymous
POST   /api/auth/verify-otp                  anonymous
GET    /api/auth/me
GET    /api/listings/nearby                  PostGIS radius search
POST   /api/listings                         GET /api/listings/me, /{id}, publish, status
GET    /api/bookings/quote                   "could I book this, and why not?"
POST   /api/bookings                         + /{id}/start, /end, /cancel
GET    /api/bookings/me, /hosting, /{id}
GET    /api/wallets/me, /me/transactions     + /{userId}, recharge, cash-out
GET    /api/wallets/{userId}/reconciliation  replay entries, compare to stored balances
GET    /api/vehicles/me                      + POST, DELETE
POST   /api/payments/orders                  start a credit purchase
GET    /api/payments/orders/{id}             poll after checkout; the redirect proves nothing
POST   /api/payments/webhook                 anonymous; the signature is the only control
GET    /sandbox/checkout/{providerOrderId}   anonymous, sandbox provider only
GET    /api/admin/pricing                    + PUT
```

Error mapping is centralised so controllers stay free of `try/catch`, and the status split is
deliberate: **401** "who are you", **403** "not yours", **400** business rule broken, **402**
renter is short on credits.

---

## 4. Database structure

PostgreSQL 16 + PostGIS. 18 tables. Money is `decimal(18,2)` everywhere, rounded through
`Money.Round` before it reaches the ledger so rounding can never unbalance a transaction.

### Tables by module

**Identity**

| Table | Notes |
|---|---|
| `users` | Unique index on `Phone`. Carries `TrustScore`, `Role`, `KycStatus`. |
| `vehicles` | FK → users, cascade. Indexed on `PlateNumber`. |
| `otp_codes` | Stores a **hash** of the code, never the code. Index `(Phone, ConsumedAt, CreatedAt)` serves the "newest unconsumed" lookup; a separate index on `ExpiresAt` lets a cleanup job drop expired rows cheaply. |

**Listings**

| Table | Notes |
|---|---|
| `parking_spaces` | Index `(City, Status)`. Plus the PostGIS column — see below. `TimeZoneId` defaults to `Asia/Kolkata` **at the database**, so the backfill on existing rows landed on a real zone rather than an empty string that would fail every availability check. |
| `space_vehicle_supports` | Unique `(ParkingSpaceId, VehicleType)`. |
| `space_photos` | Cascade from space. |
| `availability_windows` | Recurring weekly hours. Index `(ParkingSpaceId, DayOfWeek)`. |
| `availability_blackouts` | One-off closures. Index `(ParkingSpaceId, From, To)`. |

**Bookings**

| Table | Notes |
|---|---|
| `bookings` | Three indexes: `(ParkingSpaceId, StartTime, ExpectedEndTime)` drives the overlap check; `(RenterId, Status)` and `(HostId, Status)` drive the two "my bookings" lists. |
| `city_pricing_configs` | Unique `(City, Zone, VehicleType)` — one band per combination. |
| `ratings` | Unique `(BookingId, FromUserId)`: one rating per direction per booking. |
| `disputes`, `dispute_evidence` | Entities exist; no workflow built yet. |

**Money**

| Table | Notes |
|---|---|
| `wallets` | Unique on `UserId`. Three balance columns + a `Version` concurrency token. |
| `ledger_transactions` | **Unique index on `IdempotencyKey`** — the real idempotency guard. Indexed on `BookingId` and `CreatedAt`. |
| `ledger_entries` | Index `(WalletId, AccountType)` for reconciliation sums, `CreatedAt` for statements. |
| `payouts` | Index `(HostId, Status)`. |
| `payment_orders` | **Unique on `ProviderOrderId`** — a webhook lookup must resolve to exactly one order. Index `(UserId, Status)`. |

### Geo-search

`parking_spaces` carries a **generated** column:

```sql
geog geography(Point, 4326) GENERATED ALWAYS AS (
    ST_SetSRID(ST_MakePoint("Longitude", "Latitude"), 4326)::geography
) STORED;

CREATE INDEX ix_parking_spaces_geog ON parking_spaces USING GIST (geog);
```

Generated, not application-maintained, so there is no code path that can update lat/lng and forget
the geometry. The GiST index is the difference between a ~300ms "spaces near me" and a full table
scan.

The query itself does **not** go through EF — it's parameterised raw SQL in
[PostgresSpaceSearchService.cs](../src/ParkNest.Infrastructure/Persistence/PostgresSpaceSearchService.cs),
so the domain model stays provider-agnostic and testable on SQLite ([adr/0003](adr/0003-geo-search-without-nts.md)).
Every parameter is explicitly typed, because Postgres cannot infer a type for an untyped `NULL` in
the optional filters and fails the whole statement.

### The ledger's shape

Credits live in **accounts**, three per wallet and three at platform level:

| Account | Scope | Meaning |
|---|---|---|
| `Spendable` | wallet | Free credits, bookable |
| `Held` | wallet | Reserved against an open booking |
| `Earning` | wallet | Host credits awaiting cash-out |
| `PlatformRevenue` | system | Commission income |
| `ExternalFunding` | system | Real money entering on recharge |
| `ExternalPayout` | system | Real money leaving on cash-out |

The system accounts exist so every transaction has a balancing counterparty. Without them a
recharge would be single-sided and the ledger stops being double-entry.

Every movement is one `LedgerTransaction` with two or more `LedgerEntry` legs:

```
Settlement 180 (session closed, 10% commission)
  Dr  renter.Held      180
  Cr  host.Earning     162
  Cr  PlatformRevenue   18
```

**The stored `Wallet` columns are a cache. The entry stream is the truth.**
`GET /api/wallets/{userId}/reconciliation` replays the entries and compares.

### Invariants enforced twice

Once in `LedgerService`/`Wallet.Apply`, and again in the database — so a bug in the service layer,
or a hand-run `UPDATE` during a 2am incident, still cannot corrupt the ledger:

```sql
CHECK ("SpendableBalance" >= 0)   -- and Held, and Earning
CHECK ("Amount" > 0)              -- direction carries the sign, not the amount

CREATE TRIGGER trg_ledger_entries_append_only
BEFORE UPDATE OR DELETE ON ledger_entries
FOR EACH ROW EXECUTE FUNCTION parknest_reject_ledger_mutation();
```

Append-only means a mistake is corrected by posting a compensating transaction, never by rewriting
history.

### A note on SQLite in tests

SQLite has no native `DateTimeOffset`, so EF cannot translate range comparisons on it — which the
booking-overlap check depends on. `ConfigureConventions` encodes them to a binary long under the
SQLite provider only. Safe because every timestamp the application writes is UTC; Postgres keeps
its native `timestamptz` mapping and is unaffected.

---

## 5. Load handling

Honest summary: **the design has the right shapes in place, and none of it has been load-tested.**
What follows separates what exists from what we know we still owe.

### What's in place

**Every hot query has a covering index.** Nothing in the request path is a table scan:

| Query | Index |
|---|---|
| Spaces near me | GiST on `geog` |
| Overlap check on a new booking | `(ParkingSpaceId, StartTime, ExpectedEndTime)` |
| My bookings / bookings on my spaces | `(RenterId, Status)`, `(HostId, Status)` |
| OTP verification | `(Phone, ConsumedAt, CreatedAt)` |
| Webhook → order | unique `ProviderOrderId` |
| Wallet reconciliation | `(WalletId, AccountType)` |

**Idempotency is a database constraint, not a code check.** Every ledger transaction carries a
caller-supplied key under a unique index. Two concurrent retries cannot both insert. This is what
stops a renter's flaky mobile connection from double-debiting them at checkout — and it degrades
correctly under exactly the conditions (bad network, impatient user, retry storm) that *only*
appear under load.

`CreateBookingAsync` checks the hold key up front and returns the existing booking rather than
creating a second one.

**Optimistic concurrency on wallets.** `Wallet.Version` is a concurrency token, so a stale read
loses instead of silently overwriting a balance.

**Reads that don't touch the write model.** `Application.Queries` serves the list endpoints with
projections rather than loading full aggregates.

**Stateless API.** Nothing is held in process memory between requests; identity rides in the JWT.
The API scales horizontally today without a sticky-session or shared-cache story.

### Known bottlenecks — ranked

**1. Wallet contention has no retry policy.** `Version` detects a conflict; nothing handles it. The
caller gets a `DbUpdateConcurrencyException` surfaced as a 500. This is fine for a renter's own
wallet (self-contention is rare) but it is a real risk on the **platform system accounts** if we
ever give them stored balances, and on a host wallet receiving many concurrent settlements. Needs
either a bounded retry with jitter or `SELECT … FOR UPDATE`. Flagged in
[ledger-model.md](ledger-model.md) and not yet load-tested.

**2. The booking overlap check is a TOCTOU race.** `EnsureNoOverlapAsync` runs an `AnyAsync`, then
the insert happens afterwards. Two simultaneous bookings for the same space and window can both
pass the check. The fix is a Postgres exclusion constraint using `btree_gist`:

```sql
ALTER TABLE bookings ADD CONSTRAINT ex_bookings_no_overlap
EXCLUDE USING GIST (
    "ParkingSpaceId" WITH =,
    tstzrange("StartTime", "ExpectedEndTime") WITH &&
) WHERE ("Status" IN (0, 1));   -- Held, Active
```

That makes a double-booking physically unrepresentable, the same way the CHECK constraints do for
the ledger. Recommended as the next database change.

**3. `CreateBookingAsync` is not transactional across its two writes.** The hold is posted via
`PlaceHoldAsync` (which saves), then the booking row is added and saved separately. A crash between
them strands held credits with no booking attached. `IParkNestDbContext` already exposes
`BeginTransactionAsync` — it just isn't used here. Under load, "rare crash window" becomes "daily
occurrence."

**4. Geo-search opens its own connection.** `PostgresSpaceSearchService` constructs a fresh
`NpgsqlConnection` from the connection string rather than using the `DbContext`'s. Npgsql pools
these, so it isn't catastrophic, but it sits outside any ambient transaction and outside EF's
connection accounting. Worth folding back into the context connection.

**5. No rate limiting anywhere.** `POST /api/auth/request-otp` is anonymous and sends an SMS per
call. At real SMS pricing that is a direct financial DoS. `POST /api/payments/webhook` is anonymous
too — its signature check holds, but an attacker can still force an unbounded HMAC computation per
request. ASP.NET Core's built-in rate limiter should go on both before anything is publicly
reachable.

**6. No caching layer.** `docker-compose.yml` provisions Redis, but **grepping `src/` for
`Redis|StackExchange` returns zero hits** — nothing uses it yet. The obvious first candidates are
city pricing bands (read constantly, written rarely by an admin) and hot geo-search tiles.

**7. No connection-pool or retry configuration.** `AddDbContext` uses `UseNpgsql(connectionString)`
with defaults — no `EnableRetryOnFailure`, no explicit pool sizing, no `AddDbContextPool`. Fine for
one instance; needs deliberate tuning before multiple API instances share one Postgres.

**8. Overstay is billed in one shot at checkout.** PRD §10.3 wants incremental debits *during* the
session with a push notification per increment. That needs the background metering job and SignalR
(Phase 1). Relatedly, `OverstayGraceMinutes` is configured but not enforced — there is no running
timer to enforce it against.

### Where this goes

The seams are already cut for it. When the monolith needs to split, the modules map onto PRD §11
services directly, and RabbitMQ is already in `docker-compose.yml` so the local environment doesn't
have to change when Booking → Wallet → Notification decoupling lands.

The ledger is the piece that will *not* need rewriting: append-only, idempotent, replayable,
double-entry. It was built to be the thing you can still trust after everything around it has been
rearranged.

---

## 6. Running it

Postgres is the only infrastructure the API actually needs today — Redis and RabbitMQ are
provisioned in `docker-compose.yml` for Phase 1 but nothing references them.

```bash
docker compose up -d                 # or point the connection string at a local Postgres+PostGIS
dotnet tool restore                  # pins dotnet-ef 8.0.10
dotnet dotnet-ef database update --project src/ParkNest.Infrastructure --startup-project src/ParkNest.Api
dotnet run --project src/ParkNest.Api          # http://localhost:5109, Swagger at /swagger

cd clients/admin && npm install && npm start   # http://localhost:4200

dotnet test tests/ParkNest.UnitTests  # 169 tests, no Docker required
```

Payments run on the sandbox gateway in Development, so buying credits works out of the box: press
**Add credits** on the wallet page, then **Pay** on the checkout sheet. Nothing is charged. Switch
`Payments:Provider` to `Razorpay` with real credentials when you have them.

Outside Production the API returns the OTP in the response body and the login screen displays it,
because no SMS gateway is wired. For the pricing screen, add your number to `Auth:AdminPhones`
before first sign-in.

Platform economics are configuration, never constants — commission rate, billing increment, grace
period, cash-out floor all live in the `Platform` section of `appsettings.json`. City price bands
are database rows managed through `/api/admin/pricing`.
