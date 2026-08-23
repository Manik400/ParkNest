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

- [x] **KYC end to end.** `RequestCashOutAsync` had always refused a host who was not verified, and
      nothing in the system could ever verify one — a host could earn credits and never convert
      them. A host now submits name, document and a photograph; an operator reviews it in the
      console; approving is what opens cash-out. The document number is not stored: four
      characters for a human to match against the photograph, and a keyed hash so the same
      document arriving under a second account is visible.
- [x] **Cash-out in the app.** Hosts could not withdraw from the phone at all. The wallet now shows
      earnings, and either offers cash-out or explains which check is in the way.
- [x] **Listing photos in the clients.** Uploaded and served since Phase 0, reachable from neither
      client. Hosts add and remove them from the app; renters see them above the booking form.
- [x] **Double-booking settled by the database.** The overlap check read and then wrote, so two
      requests could pass it in the same instant and both insert — a renter driving to a bay that
      is taken. A Postgres exclusion constraint over `(space, [start, end))` for live bookings is
      the guarantee now; the application check stays for the better message.
- [x] **The hold and the booking are one transaction.** Two separate commits left a window where
      credits were held against a booking row that never landed: the renter's money frozen behind
      something they could not see, cancel or be refunded for.

### What is actually left in Phase 0

- Razorpay against the live API, which needs credentials and an escrow arrangement that does not
  exist yet. Neither is a code problem, and it is the only item left.
- The payout leg of that arrangement with it: an operator records each transfer by hand, which is
  the honest shape until there is an aggregator to call.

## Phase 1 — Trust and scale hardening

- [x] Background overstay meter: incremental debits *during* the session, not one shot at checkout
      (PRD §10.3). `OverstayGraceMinutes` now governs when the meter starts rather than sitting
      unused. It composes with checkout by accumulating — the meter records what it has taken, and
      checkout charges the difference — so a session costs the same whether the meter ran every
      increment, once, or never.
- [x] RabbitMQ: booking, dispute and wallet events published to a topic exchange, consumed into a
      notification module. In-process dispatch is the default and not a stopgap — the modules are
      in one process, so a broker between them buys nothing until they are not, and requiring one
      would mean installing RabbitMQ to see a booking confirmation.
- [x] SignalR: session start and end, overstay warnings, wallet movements and cancellations pushed
      to a per-user group. Every push is paired with a stored notification rather than replacing
      it — a socket message to a phone in a basement is simply lost. FCM now pushes off the same
      stored notification, so the same message reaches a handset that is not connected at all.
- [x] Tier 2 detection: QR scan + GPS geofence, opt-in per space. The host mints a code for the
      sticker; a check-in must present that code *and* a position near the pin. The app now does
      both ends of it: the host draws the sticker's QR on their own handset, and the renter scans it.
- [x] Ratings and trust score beyond the current violation penalty. Both parties rate a finished
      session, once each; scores move the trust score in small steps because it gates cash-out and
      one annoyed counterparty must not be able to strand a host's earnings. An unrated user has no
      average rather than a zero — "0.0 stars" for a new host reads as terrible, which is the
      opposite of the truth.
- [x] Prometheus: booking volume, settled credits, shortfall, dispute rate, and an hourly sweep
      that replays every wallet from its ledger and publishes the drift as a gauge. Counters are
      fed off the same events as notifications rather than instrumented inside the money paths.
      The dashboards that read them now live in [ops/](../ops/README.md).
- [x] Wallet concurrency under load. It needed both, and neither alone was enough: a retry on the
      optimistic token, and a `FOR UPDATE` lock on the wallet rows so writers queue instead of
      colliding. Tested against real Postgres, because a suite on one shared SQLite connection can
      only carry a concurrency token, never exercise it.

- [x] **FCM push.** Device tokens register per install and move between accounts rather than
      duplicating, so a borrowed handset stops receiving the previous owner's bookings. The push
      is sent from the same place the durable notification is written, after it commits, and
      carries the same title and body — the two can never say different things about one event. A
      provider outage costs a buzz, not a message, and a token Firebase disowns is pruned on the
      spot. `Push:Provider=None` is the default and is allowed in Production.
- [x] **Grafana dashboards.** [ops/](../ops/README.md): a provisioned Prometheus and Grafana in
      `docker compose`, one dashboard in the order PRD §17 asks the questions, and three alert
      rules. Ledger drift is the critical one and should be exactly zero.
- [x] **Client gap closed on the phone.** Tier 2 scanning, ratings and notifications are all in the
      Flutter app now:
      - scanning the sticker takes a precise fix and sends both halves or neither — a scan with no
        location is a photographed sticker, which is what the geofence exists to catch;
      - the host draws their own QR on the handset, so the token never travels through an image
        URL or the caches behind one;
      - a finished session asks the server whether a rating is still owed rather than working the
        rule out locally;
      - notifications are a list with an unread badge, polled once a minute because the app holds
        no socket.

- [x] **The trust score gates something.** It moved on ratings and violations and was read by
      nothing, which made it a number the system computed and never used. Cash-out is the right
      place for it to bite — money leaving is the movement nothing can compensate for cheaply — and
      the floor sits far below what one annoyed counterparty can do.
- [x] **Dispute evidence attachments** (PRD §18, Phase 1). Evidence was a URL field asking the
      complainant to host the photograph of their own blocked driveway somewhere else and keep it
      alive until a reviewer looked. Both parties now upload from the app while the dispute is
      undecided, and the operator sees the pictures next to the decision.
- [x] **Alertmanager.** Firing rules now reach a receiver, with grouping, repeat intervals and a
      critical that suppresses the warnings beneath it. Routed to a local sink by default —
      see [ops/](../ops/README.md).
- [x] **Ratings in the console.** A dispute now shows both parties' star average, trust score and
      recent comments before the decision. Shown for both on purpose: a dispute where only the
      complainant is looked up is a dispute decided on who complained.

- [x] **FCM in the app.** `PushService` initialises Firebase, asks for the permission after
      sign-in rather than at launch, registers the token on every start because the platform
      rotates it whenever it likes, re-registers on refresh, and unregisters on sign-out — before
      the session is cleared, because that endpoint is authorised. A foreground push moves the
      bell's badge immediately instead of waiting up to a minute for the poll. The Google Services
      Gradle plugin is applied only when `google-services.json` is present, so a clone with no
      Firebase project still builds and runs; `Firebase.initializeApp` throwing is caught and
      leaves push disabled with everything else intact.
- [x] **Who is on call.** [ops/on-call.md](../ops/on-call.md): the rota, how to wire a real
      receiver, and what to do when each of the three rules fires — including the rule that the
      ledger is never corrected by editing a balance.

### Left over from Phase 1

- **A Firebase project.** The app is wired end to end and degrades cleanly without one. What is
  missing is an account: create the project, download `google-services.json` into
  `clients/mobile/android/app/`, and push starts working on the next build. It is git-ignored, so
  one developer's project cannot silently become everybody's.
- **Names in the rota.** `ops/on-call.md` has the structure and the response steps; the three
  rows are deliberately blank. A placeholder looks answered, which is worse than an empty row.

## Phase 2 — Multi-city

- [x] **Redis caching for geo-search results.** A decorator over the PostGIS query, left out of the
      chain entirely when caching is off rather than registered and told to stand aside. Three
      providers: `None` (the default — one indexed query is inside its latency budget for a single
      city), `Memory` (the whole benefit on a single instance, and stale on every other one), and
      `Redis` (the multi-city answer). The origin is rounded into a grid before it becomes a key,
      because without that a phone's GPS jitter mints a new key per tap and the cache never hits;
      the cost is that distances are measured from the rounded point, which is why the precision is
      configuration. Every filter is in the key. Invalidation moves a generation counter rather
      than deleting keys — a listing appearing or being withdrawn changes the answer for every
      origin within its radius, and those keys were built from coordinates nobody recorded.
- [x] **Dynamic band management with audit history.** Bands change without a deploy, which is the
      point of them, and therefore without a commit — so `pricing_band_changes` is the only record
      of how a city's price ceiling got where it is. Append-only, one row per edit, holding both
      sides of it, who made it and why. City and zone are copied onto the row rather than joined,
      so the history outlives the band. Deactivation is its own kind because it stops every listing
      in that zone validating. The console shows the trail under the bands, scoped to one or across
      the platform.
- [ ] KYC flow end to end; escrow partnership finalised — KYC itself shipped in Phase 0
      (submission, operator review, verification gating cash-out). The escrow partnership is the
      open half, and it is a commercial arrangement rather than code.
- [ ] B2B pilot: housing societies and office parks. Not a code item — it needs pilot sites.

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
