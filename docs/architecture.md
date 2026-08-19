# Architecture as built

PRD §11 describes a microservice decomposition. What exists today is a **modular monolith** with
the same seams — see [adr/0001-modular-monolith.md](adr/0001-modular-monolith.md).

## Layers

```
ParkNest.Api            Controllers, error → HTTP mapping. No business logic.
      ↓
ParkNest.Application    Use cases. Ledger, wallet, pricing, listings, bookings.
      ↓
ParkNest.Domain         Entities and invariants. Zero dependencies.

ParkNest.Infrastructure EF Core, Postgres/PostGIS, migrations, DI wiring.
                        Implements Application's interfaces; nothing depends on it but the API.
```

The dependency rule is one-directional: `Application` knows `Domain`, `Infrastructure` knows
`Application`, `Api` knows `Infrastructure`. `Application` talks to persistence through
`IParkNestDbContext`, which is why the test suite can swap Postgres for SQLite wholesale.

## Modules and their PRD counterparts

| Module | Namespace | Future service (PRD §11) |
|---|---|---|
| Wallet / Ledger | `Application.Wallets` | Wallet / Ledger Service |
| Booking / Session | `Application.Bookings` | Booking & Session Service |
| Pricing rules | `Application.Pricing` | Pricing Rules Service |
| Listings + geo-search | `Application.Listings` | Listing & Geo-Search Service |
| Identity | `Application.Auth` | Auth & Identity Service |
| Notifications | *not built* | Notification Service |
| Payments | `Application.Payments` | Payment Service |
| Disputes | `Application.Disputes` | Admin / Dispute Service |

Only the wallet module may mutate a balance. Booking decides *timing*; wallet decides *money*.
That split is deliberate — it keeps every credit movement in one auditable place and is why the
booking service has no arithmetic on wallet columns anywhere in it.

## Data flow: a session, end to end

```
POST /api/bookings              → quote from city band → PlaceHold → Booking(Held)
POST /api/bookings/{id}/start   → Booking(Active), meter starts
POST /api/bookings/{id}/end     → measure actual duration
                                   ├ under  → ReleaseHold(unused) → Settle(used)
                                   └ over   → DebitOverstay(excess) → Settle(hold + covered)
                                                └ uncovered → Booking(InViolation), trust score −10
```

## Persistence notes

- **Geo-search** does not go through EF. `parking_spaces` carries a generated
  `geography(Point,4326)` column with a GiST index, queried by parameterised raw SQL in
  `PostgresSpaceSearchService` — see [adr/0003-geo-search-without-nts.md](adr/0003-geo-search-without-nts.md).
- **Ledger invariants** are enforced twice: in `LedgerService`/`Wallet.Apply`, and again as
  Postgres `CHECK` constraints plus an append-only trigger.
- **Sessions** live in `refresh_tokens`, hashed. A JWT cannot be withdrawn once signed, so access
  tokens are short and the revocable half of the session is the row in that table. Rotation leaves
  a chain: presenting an already-spent token revokes every token descended from the same sign-in.
- **Money** is `decimal(18,2)` everywhere, rounded through `Money.Round` before it reaches the
  ledger so rounding can never unbalance a transaction.

## What is deliberately missing

RabbitMQ eventing, SignalR live sessions, the background overstay meter, and the Flutter renter
app. All Phase 1+ — see [backlog.md](backlog.md).

The payment gateway is written and exercised end to end against the built-in sandbox, but has
never talked to a live aggregator; the escrow arrangement behind it does not exist yet either.
That is a commercial gap rather than a code one, and it is the last thing standing between the
recharge path and real money.

Authentication used to be listed here. It landed in `b48a596`: phone + OTP issuing JWTs,
deny-by-default authorisation, and no user id anywhere in a request body — the acting user comes
from the token. Refresh tokens and rate limiting followed.
