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
- [x] **OTP rate limiting.** Per phone number in the service (resend cooldown plus a rolling
      window cap), and per caller at the HTTP edge on request-otp, verify-otp and the payment
      webhook. Two layers because they stop different attacks.
- [x] **Refresh tokens and revocation.** Access tokens now live an hour; the session lives on a
      stored refresh token that rotates on every use. Replaying a spent token revokes the whole
      family descended from that sign-in. Signing out ends the session server-side.
- [x] **Real SMS gateway.** MSG91, chosen for Indian DLT compliance. Startup still fails in
      Production if no real sender is configured.
- [x] **Recharge via aggregator webhook only.** Credits are now issued solely by a signature-verified
      webhook, for the amount recorded server-side at order time, under an idempotency key derived
      from the order id. See [adr/0004](adr/0004-credits-are-not-a-wallet.md).
- [x] **A gateway that can actually be run.** `Payments:Provider=Sandbox` serves its own checkout
      page and signs its own callbacks, so the whole path works with no account and no network;
      refused in Production. See [adr/0006](adr/0006-sandbox-payment-gateway.md).
- [x] **Payment order expiry.** A hosted service cancels orders left unpaid past the window. A
      verified callback on a cancelled order still credits — expiry is our bookkeeping, not the
      gateway's.
- [x] Payout failure handling: a rejected transfer posts a compensating `Refund` returning the
      credits to the host's earning balance, and admin endpoints record the outcome either way
- [x] **Availability enforcement.** Windows and blackouts are now checked at booking time, in the
      space's own IANA time zone. Overnight windows and 24/7 spaces are handled.
- [x] Integration test against a real PostGIS container, covering geo-search: radius, ordering,
      distance in metres, the filters, and that the generated `geog` column tracks an edit
- [x] Admin dispute console endpoints + resolution posting a compensating transaction
- [ ] Razorpay integration exercised for real. The code is written and unit-tested, but it has
      never talked to Razorpay's API — the sandbox proves the shape, not their particular JSON.
      The escrow/aggregator arrangement itself is still unestablished.
- [x] Flutter app: renter + host in one app. Sign-in, find a space, quote and book, run the
      session, wallet with credit purchase, vehicles, hosting, disputes. Device location and a
      map are the notable absences — the search screen takes coordinates for now.
- [x] Angular admin panel: pricing bands, disputes and payouts all present

- [x] Device location and a map in the app. Search opens on the device's position and falls back
      to the city centre, naming which of GPS-off / denied / no-fix happened. OpenStreetMap tiles
      through flutter_map, price-as-pin.
- [x] Listing a space from the phone, pin placed on the same map. Publish stays a separate call,
      so a price outside the city band leaves a draft to fix rather than a lost form.

### What is actually left in Phase 0

- Razorpay against the live API, which needs credentials and an escrow arrangement that does not
  exist yet. Neither is a code problem.
- Razorpay is the only remaining item, and it is not a code problem.

## Phase 1 — Trust and scale hardening

- [x] Background overstay meter: incremental debits *during* the session, not one shot at checkout
      (PRD §10.3). `OverstayGraceMinutes` now governs when the meter starts rather than sitting
      unused. It composes with checkout by accumulating — the meter records what it has taken, and
      checkout charges the difference — so a session costs the same whether the meter ran every
      increment, once, or never.
- [ ] RabbitMQ: publish booking events, consume in wallet and notification modules
- [ ] SignalR: live session timer, overstay warnings, wallet updates; degrade to FCM push when the
      socket drops
- [x] Tier 2 detection: QR scan + GPS geofence, opt-in per space. The host mints a code for the
      sticker; a check-in must present that code *and* a position near the pin. Renter-facing
      scanning in the app is still to do — the API accepts it, nothing photographs a QR yet.
- [ ] Ratings and trust score beyond the current violation penalty
- [ ] Prometheus/Grafana: booking volume, ledger reconciliation drift, dispute rate
- [x] Wallet concurrency under load. It needed both, and neither alone was enough: a retry on the
      optimistic token, and a `FOR UPDATE` lock on the wallet rows so writers queue instead of
      colliding. Tested against real Postgres, because a suite on one shared SQLite connection can
      only carry a concurrency token, never exercise it.

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

- **Overstay when the next booking is already due.** Nothing prevents a renter overstaying into
  another renter's booked slot. The credit system bills them, but the second renter still has
  nowhere to park. Needs a product decision — refund and re-house, or let the second renter take
  the loss and compensate them — before it is worth writing.

## Decided, and configurable

- **Cancellation policy.** Free up to `Platform:FreeCancellationMinutes` before the booked start;
  inside that window the renter forfeits `Platform:LateCancellationFeeRate` of the hold, which
  settles to the host exactly as a session would. Defaults are an hour and half the hold, chosen
  as a starting position rather than from evidence — the whole point of it being configuration is
  that ops can move it once there is some. Zero restores the old behaviour.
