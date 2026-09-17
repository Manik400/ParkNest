# ADR 0007: Provider-selectable payment gateways

**Date:** 2026-09-11
**Status:** Accepted. Amends [0006](0006-sandbox-payment-gateway.md).

## Context

The wallet has to be loaded with real money at the lowest possible cost. The research is in
[payment-gateway-setup-fully-free-rnd.md](../payment-gateway-setup-fully-free-rnd.md). Its conclusions:
- Nothing settles real money for free forever; a licensed aggregator has to move it.
- Stripe and PayPal cannot be used by an Indian individual for domestic payments.
- The cheapest legitimate route is a gateway's zero-fee window for new merchants, and the best gateway
  changes over time.

So the choice of gateway has to be cheap to make and cheap to change. The code did not allow either:

- **`AddPayments` ignored `Payments:Provider`.** Any provider with keys got Razorpay.
- **No real provider returned a URL.** Razorpay returns keys for its JavaScript SDK. The admin site
  stopped at "order created" and the mobile app showed an error. Neither client had any provider SDK,
  and adding one per provider per client is the wrong direction.
- **Verification assumed one signature header.** Providers disagree: Razorpay signs the body in one
  header; Cashfree signs timestamp + body across two headers; PhonePe sends a fixed
  `Authorization` hash that covers credentials, not the body.
- **Webhooks were the only way to learn a payment succeeded.** In local development there is no
  public URL, so they never arrive. In production they get lost during deploys and outages. A user
  whose money had moved would have no credits until someone noticed.
- **The return URL was hard-wired to the admin site.** A payment started on a phone came back to
  `localhost:4200`, which a phone cannot reach.

## Decision

1. **`Payments:Provider` selects the gateway**, and each provider has its own configuration section
   (`Payments:Razorpay:*`, `Payments:Sandbox:*`, …). `PaymentRegistration` refuses to start on:
   - an unknown provider;
   - a half-filled section (the error names the missing keys);
   - the sandbox, no provider, `Mode=Test` or a non-https `PublicBaseUrl` in Production;
   - a Razorpay key whose prefix disagrees with `Mode`;
   - the retired flat keys (`Payments:KeyId`, …).
2. **Every checkout payload carries a `checkout_url`.**
   - A provider with a hosted page returns that page's address.
   - A JavaScript-only provider (Razorpay, Cashfree) implements `IHostedCheckoutPage`, and the API
     serves `/checkout/{providerOrderId}`: a small page that loads the provider's script and opens
     its sheet.
   - Clients open a URL and never embed a provider SDK.
3. **Webhook verification gets every header** (`WebhookRequest`). Each adapter reads what its
   provider sends.
4. **Asking the gateway is a second trusted source.** `IPaymentGateway.QueryOrderAsync` calls the
   provider's own API over TLS, which is as trustworthy as a signed webhook. `PaymentReconciler`
   asks at three points:
   - the browser's return trip;
   - a client's poll, once the order is 15 s old and at most every 10 s, tracked by
     `PaymentOrder.LastCheckedAt` so polling never becomes a call to the provider per poll;
   - the background sweep, before it expires anything.
5. **One crediting path.** The webhook, the return trip, the poll and the sweep all go through
   `PaymentSettlement.ApplyAsync`.
   - The ledger's idempotency key is `payment:{orderId}`, so however these paths race, an order
     credits exactly once, and only for the amount on our own record.
   - A gateway-reported amount that disagrees with the order stops the credit and logs Critical.
6. **A webhook that proves only who sent it is confirmed before it is applied.** An adapter sets
   `WebhookIsAuthoritative = false`, and the service applies the gateway's status answer instead of
   the body.
7. **The return endpoint cannot be an open redirect.**
   - The provider is given `/checkout/return/{orderId}`, which contains no client-supplied data.
   - The order records which client started it (`PaymentOrder.ReturnTo`: `admin` or `app`).
   - The endpoint redirects only to that client's URL in `Payments:ReturnUrls`.
   - The app gets a "return to ParkNest" page instead, since it is polling anyway.
8. **Adapters use raw `HttpClient`, not provider SDKs.** This matches the existing adapters, adds no
   dependencies, and every adapter is testable through its real request and parse code with a
   stubbed handler (`RecordingHttpHandler`).

## Consequences

- **Adding a provider** means one adapter class, one options section, one registration branch, its
  tests and a README section. Switching providers is a configuration change
  (`scripts/connect-payments.ps1`).
- **Missed webhooks no longer matter much.** Local development works against a provider's sandbox with
  no tunnel, because the return trip credits the payment. A tunnel (`cloudflared`) is needed only to
  watch webhooks themselves.
- **Webhooks are no longer the only way credit is issued.** ADR 0004 says credits are issued "solely
  by a signature-verified webhook". That now reads "by a verified webhook or the gateway's own
  answer when asked". Both are authenticated statements from the licensed partner; the amount still
  comes only from our own record.
- **Payment events we don't act on** are `Pending` and change nothing. This includes an
  authorised-but-uncaptured Razorpay payment, which previously marked the order Failed; now the
  capture that follows can still credit.
- **Recoverable failures.** A failed attempt on an order can be followed by a successful retry on the
  same order, and it credits. When asked, Razorpay reports failed attempts as Pending rather than
  failing the order.
- **Migration** `AddPaymentReconciliation` adds `payment_orders.ReturnTo` and `LastCheckedAt`.
- **Cashfree adapter (2026-09-12).** `CashfreePaymentGateway` is the recommended first real
  provider: no setup or annual fee, test keys before KYC, 0% offer for new merchants. Where it
  differs from Razorpay: the order is named with our own id, so `ProviderOrderId` is our order id;
  amounts are rupees, not paise; the webhook signature is Base64 HMAC-SHA256 over
  `timestamp + body` keyed with the client secret, so there is no separate webhook secret; and the
  hosted page fetches the `payment_session_id` from Cashfree when it renders rather than storing it.
  Cashfree requires the payer's mobile number, so `StartPaymentRequest` gained an optional `Phone`
  for accounts without one; the adapter refuses (`PaymentPhoneRequiredException`) rather than
  sending a made-up number, and both clients ask and retry. A typed number is kept as
  `User.PaymentPhone` (migration `AddPaymentPhone`) and shown on a Profile page. It is not the
  sign-in phone: that stays verified-only, since an unverified number that could sign in would
  hand the account to whoever holds the number.
- **Not done yet:** a PhonePe adapter, if its offer terms ever turn out to be worth it.
- **Sandbox (ADR 0006) is unchanged in spirit.** Its secret moved to `Payments:Sandbox:WebhookSecret`
  and its checkout page returns through `/checkout/return`, so it exercises the same trip a real
  provider does.
