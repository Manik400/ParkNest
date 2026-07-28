# ADR 0004 — Credits are an accounting layer, not a wallet we issue

**Date:** 2026-07-28
**Status:** Accepted (architectural intent; the integration is not built yet)

## Context

PRD §16 flags the problem plainly: a stored balance that converts back to real cash looks like a
Prepaid Payment Instrument under RBI rules, which is a licensed activity. Retrofitting compliance
after volume arrives is far harder than designing for it now.

## Decision

ParkNest's credits are an **internal accounting representation on top of a licensed partner's
escrow/nodal account**. The platform does not hold user funds in its own name.

Concretely, the code is structured so that:

- Real money crosses the boundary exactly twice — `Recharge` and `Payout` — and both are modelled
  as movements against *system* accounts (`ExternalFunding`, `ExternalPayout`) rather than as
  balances ParkNest owns.
- Those two transaction types are the only integration points with a payment aggregator. Every
  other movement is internal arithmetic between users.
- `RequestCashOutAsync` refuses to run without `KycStatus.Verified`, and `Payout` stores only a
  `BankDetailsRef` token — never bank details.

## Why

Keeping the aggregator boundary at exactly two transaction types means the compliance posture is a
property of two code paths, not of the whole system. Whichever partner is chosen (Razorpay,
Cashfree, or an escrow provider), the ledger does not change shape.

## Consequences

- Recharge currently has a direct API endpoint for development convenience. **In production it
  must be driven by the aggregator's webhook only** — a client-callable recharge that credits
  without a verified payment is a fraud hole. This is called out in the backlog and in the
  controller's own summary.
- Payout is recorded as `Requested` and the ledger debit happens immediately, so credits cannot be
  double-spent while the transfer is in flight. A failed payout must post a compensating `Refund`
  transaction; that handler does not exist yet.
- Nothing here is legal advice. PRD §16 is right that this warrants a fintech lawyer before
  recharge and payout volume scales — the point of this ADR is only that the architecture will not
  have to be torn up when that conversation happens.
