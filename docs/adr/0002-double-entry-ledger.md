# ADR 0002 — Double-entry ledger with system accounts

**Date:** 2026-07-28
**Status:** Accepted

## Context

PRD §5.1.7 calls for an append-only double-entry ledger. The naive implementation — a `balance`
column plus a log of "what happened" — is not double-entry: nothing forces the log to agree with
the column, so the two drift and there is no way to tell which is wrong.

## Decision

Model six accounts (three per wallet, three platform-level system accounts) and require every
transaction to consist of two or more legs whose debits equal their credits. Wallet balance
columns are a cache of the entry stream, and `RecomputeFromEntriesAsync` can rebuild them at any
time.

`ExternalFunding`, `ExternalPayout` and `PlatformRevenue` exist purely to give recharges, payouts
and commission a balancing counterparty.

## Why

Because it makes whole classes of bug impossible rather than unlikely:

- Credits cannot be created or destroyed by a bug — an unbalanced transaction throws before any
  write happens.
- Disagreement between stored balance and history is detectable by replay, and the reconciliation
  endpoint makes it a monitorable metric rather than an incident discovered by a user.
- Refunds and dispute adjustments are compensating transactions, so the audit trail of *what was
  originally wrong* survives the correction. PRD §16 flags that regulators may take an interest;
  a ledger you can replay is the difference between an afternoon and a month.

## Consequences

- More rows. A settlement writes three entries where a naive design writes one. This is fine —
  entries are small, append-only, and never updated.
- Every money operation must be expressed as postings. Contributors who reach for
  `wallet.Balance -= x` need to be redirected; `LedgerService` is the only writer.
- The database enforces the invariants independently (CHECK constraints, append-only trigger), so
  a bug in the C# layer still cannot corrupt the ledger.

## Alternatives rejected

**Event sourcing / CQRS** (PRD §14.2) gives the same auditability plus temporal queries, at the
cost of projections, replay tooling and a much steeper on-ramp. Double-entry over a relational
table gets most of the benefit now. Revisit if ledger throughput becomes the bottleneck.
