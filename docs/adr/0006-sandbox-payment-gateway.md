# 6. A sandbox gateway that ships in the product

Date: 2026-08-04

## Status

Accepted.

## Context

The recharge path is the only way real money becomes credits, and until now none of it could be
run. `Payments:Provider=None` made every payment endpoint throw, so the order record, the webhook
verification, the ledger posting and the client flow were all written but never exercised together.

The obvious fix is Razorpay test mode. It is free and unlimited, but it is not *frictionless*: it
needs a merchant signup, it needs KYC before the dashboard is fully usable, and — the part that
actually hurts — webhooks are outbound HTTP from Razorpay's servers to yours, so driving the flow
on a laptop needs ngrok or an equivalent tunnel. None of that can run in CI, and none of it works
on a train.

We looked for an open-source gateway to self-host. There isn't one, and there cannot be: settling
funds requires a licensed payment service provider. Everything marketed that way is either a
client-side SDK for a commercial PSP, or a mock.

## Decision

Ship the mock, and make it good.

`SandboxPaymentGateway` is a third `IPaymentGateway` implementation that runs in-process. It:

- issues its own provider order ids and a checkout URL,
- serves a hosted checkout page at `/sandbox/checkout/{providerOrderId}` with **Pay** and
  **Simulate a declined payment**,
- signs its callbacks with HMAC-SHA256 over the raw body, and
- emits the same event envelope as Razorpay, including the `payment.captured` versus
  `payment.authorized` distinction.

The page posts the signed callback to the real `/api/payments/webhook`. Nothing downstream knows
it is a sandbox: the signature is verified by the same code, the amount still comes from our own
order record, and the credits are issued by the same idempotent ledger transaction.

It is refused at startup in Production.

## Consequences

**The recharge path is now testable end to end with no account, no network and no tunnel.** The
sandbox is the default in Development. CI can exercise the whole flow.

**Reproducing the protocol, not just the outcome, is the point.** A mock that simply credited a
wallet would prove nothing. This one can only credit by producing a signature our verification
accepts, so the tests written against it are statements about the real integration — which is why
`SandboxPaymentTests` covers tampering, replay, cross-instance signatures and uncaptured payments
rather than only the happy path.

**It is a credit-minting oracle by construction.** Anyone who can open the checkout page can pay
for free, because the thing signing the callback is the thing verifying it. That is not a flaw to
be fixed; it is what a gateway with no money behind it necessarily is. The mitigations are that
`PaymentOptions.IsConfigured` deliberately excludes it, and that
`AddPayments` throws on `Sandbox` in Production.

**Signing keys are per-process by default.** With no `WebhookSecret` configured the sandbox
generates a random key at construction, so nothing needs to go in source control and an abandoned
checkout page stops being replayable after a restart. This forces the registration to be a
singleton — a scoped one would sign with one key and verify with another. Set `WebhookSecret`
explicitly if you need signatures stable across restarts, e.g. to replay a captured body from a log.

**Two things had to change to make providers genuinely swappable.** The webhook endpoint read
`X-Razorpay-Signature` from a hard-coded string; it now asks `IPaymentGateway.SignatureHeader`.
And `PaymentOptions.IsConfigured` now means "a real gateway with real credentials" rather than
"not None", so the sandbox cannot stand in for a configured provider in a check that exists to
decide whether real money can move.

## Alternatives considered

**Razorpay test mode only.** Rejected as the *only* option because it cannot run in CI or offline.
It remains fully wired and is the right choice the moment there are credentials — that is the point
of keeping the sandbox behind the same interface.

**Stripe.** Better developer experience, but Razorpay was already chosen in PRD §14.1 for India-first
UPI support, and domestic Indian payments are the market.

**A test-only fake, not shipped.** `PaymentTests` already has one. It proves the service handles a
gateway correctly; it cannot prove the HTTP endpoint reads the right header, the raw body survives
buffering, or the client flow works. Those are precisely the bugs that only appear end to end.
