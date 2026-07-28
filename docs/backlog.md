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
- [x] 26 service-level tests on in-memory SQLite
- [x] Docker Compose: Postgres/PostGIS, Redis, RabbitMQ

### Next up
- [ ] **Auth & identity.** Every endpoint is open right now. `RenterId`/`HostId` must come from a
      token, not the request body. Blocking anything user-facing.
- [ ] **Recharge via aggregator webhook only.** The current direct endpoint credits without a
      verified payment — a fraud hole if it survives into production. See
      [adr/0004](adr/0004-credits-are-not-a-wallet.md).
- [ ] Payout failure handling: compensating `Refund` transaction when the aggregator rejects
- [ ] Availability enforcement — `AvailabilityWindow` and `AvailabilityBlackout` are modelled and
      persisted but the booking path does not check them yet
- [ ] Integration test against a real PostGIS container, covering geo-search (currently untested —
      see [adr/0003](adr/0003-geo-search-without-nts.md))
- [ ] Admin dispute console endpoints + resolution posting a compensating transaction
- [ ] Razorpay/Cashfree integration behind an escrow/aggregator model
- [ ] Flutter app: renter + host, role-based views
- [ ] Angular admin panel: pricing bands, disputes, payouts

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
- **Timezones.** Availability windows use `TimeOnly` in the space's local time, but no timezone is
  stored on the space. Single-city hides this; multi-city does not.
