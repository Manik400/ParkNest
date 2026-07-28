# The credit ledger

The one part of ParkNest where "mostly right" is not acceptable. Read this before changing
anything under `ParkNest.Application/Wallets`.

## Accounts

Credits live in accounts. Three per wallet, three at platform level.

| Account | Scope | Meaning |
|---|---|---|
| `Spendable` | wallet | Free credits, bookable |
| `Held` | wallet | Reserved against an open booking. Not spendable, not yet the host's |
| `Earning` | wallet | Host credits awaiting cash-out |
| `PlatformRevenue` | system | Commission income |
| `ExternalFunding` | system | Real money entering on recharge |
| `ExternalPayout` | system | Real money leaving on cash-out |

The system accounts exist so every transaction has a balancing counterparty. Without them, a
recharge would be a single-sided entry and the whole ledger stops being double-entry.

## Transaction shapes

Every movement is one `LedgerTransaction` holding two or more `LedgerEntry` legs.
`Debit` = value leaving an account, `Credit` = value entering. Debits must equal credits.

```
Recharge 500
  Dr  ExternalFunding            500
  Cr  renter.Spendable           500

Hold 120 (booking created)
  Dr  renter.Spendable           120
  Cr  renter.Held                120

ReleaseHold 75 (left early, or cancelled)
  Dr  renter.Held                 75
  Cr  renter.Spendable            75

OverstayDebit 60 (stayed past the booked window)
  Dr  renter.Spendable            60
  Cr  renter.Held                 60

Settlement 180 (session closed, 10% commission)
  Dr  renter.Held                180
  Cr  host.Earning               162
  Cr  PlatformRevenue             18

Payout 600
  Dr  host.Earning               600
  Cr  ExternalPayout             600
```

Refunds and dispute resolutions are `Refund` / `AdminAdjustment` transactions with the legs
reversed. Nothing is ever edited or deleted.

## Invariants

1. **Balanced.** Σ debits = Σ credits, per transaction. Enforced in `LedgerService.PostAsync`;
   violating it throws `UnbalancedLedgerTransactionException` before anything is written.
2. **Non-negative.** No wallet bucket may go below zero. Enforced in `Wallet.Apply` and again by
   `CHECK` constraints in migration `PostGisAndLedgerInvariants`.
3. **Positive amounts.** Entries carry a positive amount; `Direction` carries the sign. Also a
   `CHECK` constraint.
4. **Append-only.** A Postgres trigger rejects `UPDATE` and `DELETE` on `ledger_entries`. A
   mistake is corrected by posting a compensating transaction, never by rewriting history.
5. **Idempotent.** Every transaction carries a caller-supplied `IdempotencyKey` under a unique
   index. Replaying a key returns the existing transaction instead of moving credits twice — this
   is what stops a renter's flaky mobile connection from double-debiting them at checkout.
6. **Replayable.** `SUM` over a wallet's entries must reproduce its stored balances. Verified by
   `ILedgerService.RecomputeFromEntriesAsync`, exposed at
   `GET /api/wallets/{userId}/reconciliation`, and asserted in tests.

The stored `Wallet` columns are a *cache*. The entry stream is the truth.

## Settlement arithmetic

At checkout, actual occupancy is measured from the **booked start time**, not the check-in —
arriving late doesn't shorten the window the host held open, and the space was unavailable to
anyone else regardless. That duration is rounded up to the billing increment (default 15 min).

- **Billed ≤ booked**: charge the time used, release the rest of the hold.
- **Billed > booked**: price the excess at `rate × overstayMultiplier`, pull it from spendable
  into the hold, then settle the whole hold.
- **Renter can't cover the excess**: take whatever is there, record the remainder as
  `ShortfallAmount`, mark the booking `InViolation`, and dock the trust score.

That last case is the whole point of the design. The host still receives every credit that could
actually be collected, and the platform restricts the renter's future access rather than trying
to chase cash from someone who has already driven away.

## Known gaps

- Overstay is currently billed in one shot at checkout. PRD §10.3 wants incremental debits *during*
  the session with a push notification per increment; that needs the background metering job and
  SignalR (Phase 1).
- The grace period (`OverstayGraceMinutes`) is configured but not yet enforced — there is no
  running timer to enforce it against.
- Concurrency is guarded by an optimistic `Version` token on `Wallet`. Under real contention this
  wants either a retry policy or `SELECT … FOR UPDATE`; not yet load-tested.
