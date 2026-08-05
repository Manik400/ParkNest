# Backlog

Phases follow PRD §18. Items are ordered within each phase.

## Phase 0 — MVP, single city, manual detection

### Done
- [x] Solution scaffold: Domain / Application / Infrastructure / Api + test project
- [x] Domain model for all PRD §12 entities
- [x] Double-entry ledger with system accounts, idempotency, replay ([ledger-model.md](ledger-model.md))
- [x] Wallet operations: recharge, hold, release, overstay debit, settle, cash-out
- [x] Pricing rules engine: city/zone/vehicle bands, billing increments, overstay multiplier + ceiling
- [x] Booking lifecycle: create → start → end, with settlement and violation handling
- [x] Listings: draft, publish with band validation, status changes
- [x] PostGIS generated column + GiST index + geo-search query
- [x] DB-level ledger invariants: CHECK constraints, append-only trigger
- [x] 48 service-level tests on in-memory SQLite
- [x] Docker Compose: Postgres/PostGIS, Redis, RabbitMQ
- [x] CI: build/test, migrations applied to real PostGIS, schema invariants verified
- [x] Conditional ledger guard on money paths + on-demand `@claude` PR review
- [x] Phone + OTP authentication, ownership and role checks
- [x] Availability windows + blackouts enforced at booking, per-space time zone
- [x] Read endpoints: my bookings, hosting bookings, booking detail with ledger trail,
      my listings, listing detail, wallet transaction history

### Next up
- [x] **Auth & identity.** Phone + OTP login issuing JWTs; services take the acting user from the
      token and the user id is gone from request DTOs entirely. See
      [adr/0005](adr/0005-phone-otp-auth.md).
- [ ] **OTP rate limiting.** The per-code attempt cap is in, but nothing limits how many codes a
      caller can request — an attacker can burn SMS budget freely. Needed before launch.
- [ ] **Refresh tokens and revocation.** Access tokens live 12 hours with no way to revoke one.
- [ ] **Real SMS gateway.** Only the dev logging sender exists; startup deliberately fails in
      Production until a real one is wired.
- [x] **Recharge via aggregator webhook only.** Credits are now issued solely by a signature-verified
      webhook, for the amount recorded server-side at order time, under an idempotency key derived
      from the order id. See [adr/0004](adr/0004-credits-are-not-a-wallet.md).
- [x] **A gateway that can actually be run.** `Payments:Provider=Sandbox` serves its own checkout
      page and signs its own callbacks, so the whole path works with no account and no network;
      refused in Production. See [adr/0006](adr/0006-sandbox-payment-gateway.md).
- [ ] **Payment order expiry.** `PaymentOrderStatus.Cancelled` exists but nothing sets it, so an
      abandoned checkout sits in `Created` forever. Wants a sweep.
- [ ] Payout failure handling: compensating `Refund` transaction when the aggregator rejects
- [x] **Availability enforcement.** Windows and blackouts are now checked at booking time, in the
      space's own IANA time zone. Overnight windows and 24/7 spaces are handled.
- [ ] Integration test against a real PostGIS container, covering geo-search (currently untested —
      see [adr/0003](adr/0003-geo-search-without-nts.md))
- [ ] Admin dispute console endpoints + resolution posting a compensating transaction
- [ ] Razorpay integration exercised for real. The code is written and unit-tested, but it has
      never talked to Razorpay's API — the sandbox proves the shape, not their particular JSON.
      The escrow/aggregator arrangement itself is still unestablished.
- [ ] Flutter app: renter + host, role-based views
- [ ] Angular admin panel: pricing bands done; disputes and payouts still missing

## Phase 1 — Trust and scale hardening

- [ ] Background overstay meter: incremental debits *during* the session, not one shot at checkout
      (PRD §10.3). Needs a hosted service plus the grace-period timer that `OverstayGraceMinutes`
      currently configures but nothing enforces.
- [ ] RabbitMQ: publish booking events, consume in wallet and notification modules
- [ ] SignalR: live session timer, overstay warnings, wallet updates; degrade to FCM push when the
      socket drops
- [ ] Tier 2 detection: QR scan + GPS geofence validation
- [ ] Ratings and trust score beyond the current violation penalty
- [ ] Prometheus/Grafana: booking volume, ledger reconciliation drift, dispute rate
- [ ] Wallet concurrency under load — the optimistic `Version` token is untested against real
      contention; may need a retry policy or row locking

## Phase 2 — Multi-city

- [ ] Redis caching for geo-search results
- [ ] Dynamic band management with audit history
- [ ] KYC flow end to end; escrow partnership finalised
- [ ] B2B pilot: housing societies and office parks

## Phase 3 — Advanced

- [ ] Tier 3 ANPR / occupancy sensors at pilot locations
- [ ] Event-sourced ledger, if and only if throughput demands it
- [ ] Fraud/anomaly detection on ledger patterns (wash trading, linked accounts)
- [ ] Kubernetes, if service count justifies the operational cost

## Open questions

- **Overstay when the next booking is already due.** Nothing currently prevents a renter
  overstaying into another renter's booked slot. The credit system bills them, but the second
  renter still has nowhere to park. Needs a product decision, not just code.
- **Cancellation policy.** Cancelling returns the full hold with no window or fee, so a host can
  be left with a dead slot at zero compensation.
- **Cancellation window.** Still no fee or cut-off, so a host can lose a slot at zero
  compensation minutes before it starts.
