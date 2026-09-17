# Payment gateway setup: fully free R&D

**Researched:** 2026-09-11.

**Question:** how to load the ParkNest credit wallet with real money at zero cost, ideally with open-source software. Is Stripe an option, and which provider is best?

**Status:** research complete; the provider-selectable gateway and the **Cashfree adapter are implemented** (ADR 0007). What is left is an account: sign up, paste the test keys into `scripts/connect-payments.ps1`, and confirm the 0% offer applies before going live.

> Prices and offers change. Everything below was checked on the date above against the sources in [§11](#11-sources).
> Anything marked **(unverified)** could not be confirmed from an official page. Confirm it in the provider's dashboard or with their sales team before relying on it.

---

## 1. The short answer

- **Nothing moves real money for free forever.** Moving money between bank accounts in India is a licensed activity. Only RBI-authorised payment aggregators and banks may do it. Every "open-source payment gateway" is either software that sits *in front of* one of those licensed providers, or crypto.
- **Development is already free.** ParkNest's built-in **Sandbox** gateway runs the whole recharge flow locally: checkout page, signed webhook, credits. It needs no account. Cashfree and Razorpay also issue **test keys immediately, with no KYC**.
- **Real money can be ₹0 for months.** Two gateways have zero-fee offers for new merchants:
  - **Cashfree:** 0% up to ₹20 lakh of payments, until 31 Mar 2027. Confirm eligibility; see [§3.1](#31-cashfree-recommended-first).
  - **Razorpay:** 0% for 90 days or ₹5 lakh, plus a one-time ₹199 + GST KYC fee.
- **After the offers:** about **1.95–2% + 18% GST**, which is about **₹11.5 on a ₹500 recharge**.
- **Stripe is not possible.** Stripe India is invite-only and does not accept individuals. **PayPal is not possible.** It stopped domestic INR payments in India in 2021.
- **Recommendation:**
  1. Keep developing on Sandbox.
  2. Integrate **Cashfree** first: longest free window, test keys without KYC, UPI-first.
  3. Keep **PhonePe PG** as the second adapter: simplest hosted redirect.
  4. Keep **Razorpay** as the fallback: an adapter already exists in the code.

  ParkNest's code will let you switch between them with a config change.

---

## 2. What "free" can and cannot mean

| Layer | Can it be free? | Why |
|---|---|---|
| Software on our side (checkout page, webhook handling, ledger) | **Yes.** Already written, open source, ours. | Code only. |
| A test or sandbox gateway for development | **Yes.** Our Sandbox, plus Cashfree and Razorpay test modes. | No money moves. |
| Receiving real money from users | **Only via a free window, or at ~2% after it.** | A licensed PA/bank must move the money, and they charge. |
| UPI network fee (MDR) | Currently **zero** by government policy (see [§6](#6-upi-mdr-outlook)). | Gateways still add their own "platform fee" on UPI. |
| Open-source "gateways" (Hyperswitch, Kill Bill, Lago, …) | Free **software**, but it still needs a paid gateway underneath. | They route or bill; they do not move money. |

The cheapest *legitimate* way to take real money is a licensed gateway's zero-fee window, followed by its standard rate.

---

## 3. Provider comparison

| Provider | UPI fee now | UPI fee after offer | Cards | Individual, PAN only, no GST | Free test keys before KYC | Hosted redirect page | Webhook authentication | .NET SDK | Verdict |
|---|---|---|---|---|---|---|---|---|---|
| **Cashfree** | **0%** up to ₹20L, until 31 Mar 2027 *(terms conflict, see §3.1)* | 1.95% + GST | 1.95% (0% under offer except AMEX/Diners/corporate/prepaid/EMI) | **Yes** | **Yes** | Payment Links; orders need its JS SDK (we host a small page) | `x-webhook-signature` = Base64 HMAC-SHA256(timestamp + body) | `cashfree_pg` | **Recommended first** |
| **Razorpay** | **0%** for 90 days or ₹5L; ₹199 + GST KYC fee | 2% + GST | 2% | **Yes** ("Individual/Freelancer") | **Yes** (`rzp_test_` keys) | Payment Links / `redirect:true` | `X-Razorpay-Signature` = hex HMAC-SHA256(body) | `Razorpay` | Fallback; **adapter already in code** |
| **PhonePe PG** | "1.99% FREE*, limited period" (duration and cap **unpublished**) | 1.99% | 1.99% | Probably (unregistered accounts may be capped around ₹50k/month) | Unclear (docs conflict) | **Yes**: returns a `redirectUrl` | `Authorization: SHA256(user:pass)` (does not cover the body, so confirm with the status API) | `phonepe-pg-sdk-dotnet` (.NET 8) | Good second adapter |
| Paytm PG | 1.99% on its official pricing page; "0% UPI for small merchants" appears only in marketing and third-party posts | 1.99% + GST | 1.99% | unverified | unverified | yes | checksum (SHA256 + AES) | no official one | Skip unless 0% is confirmed in writing |
| Zoho Payments | 0.5% (official FAQ) | 0.5% | 2% | unverified | unverified | Payment Links | unverified | no | Cheapest long-term rate; revisit later |
| Instamojo | 5% + ₹3 (free plan); 2% + ₹3 (Growth) | same | same | yes | — | yes | — | no | Expensive |
| PayU | "Varies by category"; one summary says NIL UPI (**sources conflict**) | — | 2% (3% Amex/EMI) | unverified | unverified | yes | — | no | Unclear |
| Easebuzz | Custom pricing (~1.5–1.95% quoted by third parties) | — | custom | unverified | unverified | — | — | no | Sales-led |
| **Stripe India** | Not available | — | 2% domestic cards | **No**: invite-only, businesses only, "can't be an individual" | not useful | — | `Stripe-Signature` | Stripe.net | **Not possible** |
| **PayPal India** | Not available | — | — | Domestic INR stopped April 2021 | — | — | — | — | **Not possible** |
| Juspay / Setu | Enterprise and sales-led | — | — | No | — | — | — | — | Not for individuals |

### 3.1 Cashfree (recommended first)
- **Offer:** 0% gateway fee on domestic payments, UPI included, up to ₹20 lakh of payment volume, until 31 Mar 2027. Zero setup fee. The pricing FAQ says there is no annual maintenance charge.
- **⚠ Conflicting terms:**
  - The terms page (pricing, `#festive-offer`) says merchants who sign up *on or after* 21 Jul 2026 qualify, with the campaign running to 31 Mar 2027. Cashfree's blog and press coverage say you had to sign up *by* 31 Jul 2026. If the blog is right, the offer is already closed.
  - The terms say ₹20L **total**, once per PAN or bank account. The blog says ₹20L **per month**.
  - The pricing table says UPI fees are "as per applicable law". The blog says UPI is explicitly covered by the 0%.
  - **Confirm eligibility in the dashboard at signup.** If the offer is closed, Razorpay's 90-day window is the next best free option.
- **Signup:** individual or sole proprietor with PAN and a bank account. GST is not required.
- **Test mode:** test keys are generated at signup; KYC is only needed for Live.
- **API:** REST with an `x-api-version` header (currently `2026-01-01`), plus `x-client-id` and `x-client-secret`. An order returns a `payment_session_id`, and the payment page is opened by Cashfree's JS SDK. ParkNest will serve a small page that loads it, so the web site and the app just open a URL.
- **Customer phone is mandatory** on orders (`customer_details.customer_phone`). ParkNest users who signed up by email will be asked for a mobile number at recharge.
- **Webhooks:**
  - Signature = Base64(HMAC-SHA256(`x-webhook-timestamp` + raw body, secret key)).
  - `notify_url` can be set per order, which is handy with a tunnel in development.
  - Events: `PAYMENT_SUCCESS_WEBHOOK`, `PAYMENT_FAILED_WEBHOOK`, `PAYMENT_USER_DROPPED_WEBHOOK`.
- **Status API:** `GET /orders/{id}` returns `PAID`, `ACTIVE` or `EXPIRED`. ParkNest also uses it to catch missed webhooks.
- **Staying free:** restrict orders to UPI (`payment_methods: "upi"`) so no card surcharge applies after the offer.

### 3.2 Razorpay (fallback, adapter exists)
- **Offer:** 0% for 90 days from activation, or the first ₹5 lakh, whichever comes first, for accounts activated on or after 1 Jul 2026. It excludes prepaid, corporate, AMEX, Diners and EMI, and applies once per PAN or bank account. There is a **₹199 + tax KYC fee**, and GST is not waived. After the offer: 2% + GST, which Razorpay words as "zero MDR, 2% platform fee applies".
- **Signup:** "Individual/Freelancer" with PAN; no GST needed.
- **Test mode:** `rzp_test_` keys, no KYC needed.
- **Checkout:** `checkout.js` with `callback_url` and `redirect:true`, or Payment Links.
- **Webhooks:** `X-Razorpay-Signature` = hex HMAC-SHA256 of the body. **Enable auto-capture in the dashboard**, otherwise payments stay "authorized", never credit, and are refunded automatically.
- ParkNest already has `RazorpayPaymentGateway`; it needs the hosted page and a status lookup.

### 3.3 PhonePe PG (second adapter)
- **Offer:** the pricing page says "1.99% FREE*, limited period" and "zero transaction fees". **Duration, cap and terms are unpublished**, so ask sales for them in writing.
- **Signup:** PAN, Aadhaar and a bank account. The website must show products, pricing, a refund policy and contact details. Unregistered businesses often get a lower monthly limit (around ₹50,000 is quoted).
- **API:** an OAuth `client_credentials` token, then `POST /checkout/v2/pay`, which returns a **`redirectUrl`**, the simplest possible checkout. The sandbox has a simulator app and the test UPI IDs `success@ybl` and `failed@ybl`.
- **Webhooks:** `Authorization: SHA256(username:password)`. This proves who sent the webhook but not what it contains, so ParkNest will confirm each webhook through the Order Status API before crediting.

### 3.4 Why not Stripe or PayPal
- **Stripe:** "Stripe accounts are invite-only in India", and the India requirements say the account "can't be an individual". Even with test mode, there is no route to going live for an individual founder. It is also card-first, while ParkNest's users pay by UPI.
- **PayPal:** stopped domestic payments within India in April 2021 and is cross-border only. An Indian user cannot pay an Indian merchant through it.

---

## 4. Open-source software: what it can and cannot do

| Project | Licence | What it is | Useful for ParkNest? |
|---|---|---|---|
| **Juspay Hyperswitch** | Apache 2.0 (Rust) | Self-hosted payment **orchestrator**: one API in front of many gateways (has Cashfree, Razorpay, PhonePe, Paytm and PayU connectors) | Not now. It still needs a paid gateway, and ParkNest's own `IPaymentGateway` abstraction does the same job at this scale. Revisit if routing across several gateways ever matters. |
| **BTCPay Server** | MIT | Self-hosted **Bitcoin** payments | **No.** Crypto is not legal tender in India, and wallet top-ups must be INR. |
| **Kill Bill** | Apache 2.0 (Java) | Subscription and **billing** engine | No. One-off top-ups need no billing engine, and it still needs a gateway. |
| **Lago** | AGPLv3 | Usage-based **billing** | No, same reason. |
| **Medusa** | MIT (Node.js) | E-commerce framework with a community Razorpay plugin | No: wrong stack, and it is not a gateway. |

**Conclusion:** the open-source part is **our own code**: the gateway abstraction, ledger, checkout pages and webhook verification. It is already written and MIT-style ours. The part that moves money can't be open source.

---

## 5. Considered and rejected: direct UPI with no gateway

The idea: show a `upi://pay?pa=<our UPI ID>&am=<amount>` link or QR code, let the user pay our bank account directly, then confirm the payment somehow. It really is ₹0 per payment, but it doesn't suit a **wallet** model:

- **Confirmation has no free API:**
  - A personal or business UPI QR gives no webhook. Payment notifications arrive only in the app or on a sound box.
  - The alternatives are the user typing the 12-digit UTR reference and an admin checking the bank statement, or parsing bank alert emails over IMAP. Email formats are undocumented, and emails can be spoofed unless the DKIM signature is checked.
  - The `tr` reference we put in the link usually doesn't come back in the bank's credit alert.
- **UPI links on iOS Safari:** there is no system app chooser. Each app has its own link scheme, and iOS doesn't return the user to the site after payment.
- **Collect requests** (asking a user's UPI ID for money) were discontinued for person-to-person payments on 1 Oct 2025 (NPCI).
- **Limits on personal UPI accounts receiving business payments:** ₹10,000 per payment, ₹25,000/day and ₹1 lakh/month for small-merchant UPI. Savings accounts receiving many UPI credits get flagged and frozen.
- **Regulation:** pooling users' money in our account and later paying hosts is what the RBI PA Directions (Sep 2025) define as payment aggregation. There is no marketplace exemption, and authorisation needs ₹15 crore net worth (see [§7](#7-legal-and-compliance-notes-read-before-going-live)).

The only direct-UPI design that is legally clean is **renter pays host directly** with no wallet. The founder chose to keep the wallet, so this is out.

---

## 6. UPI MDR outlook
- **Current:** zero MDR on UPI and RuPay debit, by government policy.
- **10 Aug 2026:** the Taxation and Other Laws (Amendment) Bill 2026 amended Section 10A of the Payment and Settlement Systems Act. The blanket zero-MDR rule becomes a list of exempt payment modes that the government will notify. **No MDR has been notified yet**, and an NPCI-led committee will decide.
- **Proposals seen:** up to 0.4% on payments over ₹2,000 to large merchants (BusinessToday), or 0.05–0.07% for turnover over ₹1–1.5 crore (Inc42). The two conflict. Small merchants are expected to stay exempt, and ParkNest's ₹100–₹500 top-ups are well below all of them.
- **Gateway "platform fees" on UPI are charged anyway**, and no RBI ruling stops them. The 0% offers above are the gateways waiving their own fee.

---

## 7. Legal and compliance notes: read before going live
*This is research, not legal advice. Get a fintech lawyer or CA to confirm before real users.*

1. **Prepaid wallet (PPI).** A **closed-system** PPI needs no RBI licence, but only if the credits can be spent **solely on the issuer's own goods and services**, with no cash-out. ParkNest credits pay third-party hosts, and hosts can cash out. That looks like a **semi-closed** PPI, which needs RBI authorisation, unless ParkNest is the **seller of record** for every booking, i.e. it sells the parking itself and pays hosts as suppliers. ADR 0004 already assumes funds sit with a licensed partner.
2. **Payment aggregation.** Collecting renters' money and later paying hosts matches the RBI PA definition. The licensed-gateway route is how this is handled. When host payouts go live, use the gateway's **split/marketplace** product (e.g. Cashfree Easy Split at about 0.20–0.25%, or Razorpay Route) rather than paying out by hand from the platform account.
3. **At gateway KYC**, describe the product accurately: closed-loop parking credits usable only on ParkNest. Gateways may treat "wallet top-up" as higher risk and set a low limit.
4. **GST.** An e-commerce operator must register for GST with no minimum turnover (Section 24). If it collects payments for suppliers, it must also collect 0.5% TCS (Section 52). Hosts supplying services are exempt from GST registration up to ₹20 lakh turnover.
5. **Bank account.** Receive settlements into a **current account in the business's name**, not a personal savings account. Savings accounts with many UPI credits get frozen.
6. **Website pages the gateways require:** pricing, refund/cancellation policy, terms, privacy policy, and contact details (address, email, phone).

---

## 8. Decision

| If you want… | Choose | Cost |
|---|---|---|
| Longest ₹0 period on real money | **Cashfree** (if eligible) | ₹0 until 31 Mar 2027 / ₹20L, then 1.95% + GST |
| The simplest checkout integration | **PhonePe PG** | "Free, limited period" (terms unknown), then 1.99% |
| The least new code | **Razorpay** (adapter exists) | ₹199 + GST once, ₹0 for 90 days / ₹5L, then 2% + GST |
| The lowest long-term rate | Zoho Payments (unverified) | 0.5% UPI |
| Zero cost while building | **Sandbox** (built in) | ₹0 forever, no real money |

**Cost after the free window**, per ₹500 recharge:
- 1.95% + 18% GST = ₹9.75 + ₹1.76 = **₹11.51**
- At ₹1 lakh of recharges per month: ≈ **₹2,300/month**
- It could be recovered as a small "convenience fee" shown to users, or absorbed from the 12% booking commission.

**Recommended path:**
1. **Now:** keep developing and testing on the built-in Sandbox (₹0).
2. **Next:** sign up for a Cashfree test account (free, no KYC) and run the Cashfree adapter against it.
3. **Before launch:** complete Cashfree KYC, confirm the 0% offer applies to your account, publish the policy pages, and settle the §7 questions.
4. **Fallback:** if Cashfree's offer isn't available, switch to Razorpay by changing config.

---

## 9. How ParkNest will integrate (summary)
Details are in `docs/adr/0007-provider-selectable-gateways.md` once written. In short:
- **Configuration:** `Payments:Provider` selects `Sandbox`, `Cashfree`, `PhonePe` or `Razorpay`. `Payments:Mode` selects `Test` or `Live`. Startup refuses unknown providers, half-filled settings, and Sandbox or Test mode in Production.
- **Checkout:** the web site and app always receive a `checkout_url`. It is either the provider's own hosted page (PhonePe) or a small ParkNest page that loads the provider's script (Cashfree, Razorpay). No provider SDK is needed in the clients.
- **Confirmation:** a verified webhook, **or** ParkNest asking the provider's status API. The status check runs when the user returns, while the client polls, and before an unpaid order expires. So a missed webhook, which is normal in local development without a public URL, still credits exactly once.
- **Secrets:** stored with `scripts/connect-payments.ps1` in `dotnet user-secrets`, never in the repo.

---

## 10. Checklist: what to have ready for signup
- [ ] PAN (individual is fine)
- [ ] Bank account; a current account in the business name is preferred
- [ ] Aadhaar (PhonePe) or another ID document
- [ ] A public website with pricing, refund/cancellation policy, terms, privacy policy and contact details
- [ ] A clear business description: "prepaid parking credits, closed-loop, usable only on ParkNest"
- [ ] **Confirm in writing:** Cashfree's 0% offer eligibility and cap; PhonePe's offer length and cap; Paytm's UPI rate if considering it

**Still unverified, to confirm at signup:**
- Cashfree: the signup deadline and whether the cap is total or monthly.
- Cashfree: whether its sandbox accepts an `http://localhost` return URL.
- PhonePe: whether test keys are available without KYC.
- PhonePe: whether the webhook hash is hex or base64.
- Paytm: whether 0% UPI is real for small merchants.
- Zoho Payments: individual signup and sandbox.
- Stripe UPI for new Indian merchants.

---

## 11. Sources
**Cashfree**
- Charges and offer: https://www.cashfree.com/payment-gateway-charges/#festive-offer
- Pricing FAQ: https://www.cashfree.com/docs/help/account/pricing
- Offer blog: https://www.cashfree.com/blog/payment-gateway-charges-india-free-payment-gateway/
- Press: https://www.tribuneindia.com/news/business/cashfree-payments-announces-zero-percent-payment-gateway-fees-for-its-new-businesses-till-march-2027/
- Unregistered businesses: https://www.cashfree.com/blog/payment-gateway-for-unregistered-businesses/
- Sandbox: https://www.cashfree.com/docs/payments/online/resources/sandbox-environment
- Create order: https://www.cashfree.com/docs/api-reference/payments/latest/orders/create
- Payment links: https://www.cashfree.com/docs/api-reference/payments/latest/payment-links/create
- Web redirect checkout: https://www.cashfree.com/docs/payments/online/web/redirect
- Webhook signature: https://www.cashfree.com/docs/payments/online/webhooks/signature-verification
- UPI intent in the JS SDK: https://www.cashfree.com/docs/payments/online/mobile/misc/upi_intent_support_js_sdk
- .NET SDK: https://www.nuget.org/packages/cashfree_pg

**Razorpay**
- Pricing: https://razorpay.com/pricing/
- 90-day offer terms: https://razorpay.com/terms/90-day-free-pg-offer/
- API keys: https://razorpay.com/docs/payments/dashboard/account-settings/api-keys/
- Payment links: https://razorpay.com/docs/api/payments/payment-links/create-standard/
- Hosted checkout: https://razorpay.com/docs/payments/payment-gateway/web-integration/hosted/integration-steps/
- Webhook validation: https://razorpay.com/docs/webhooks/validate-test/
- Fetch payments for an order: https://razorpay.com/docs/api/orders/fetch-payments/

**PhonePe**
- Payment gateway: https://business.phonepe.com/payment-gateway
- Pricing: https://www.phonepe.com/business-solutions/payment-gateway/pricing/
- Create payment: https://developer.phonepe.com/payment-gateway/website-integration/standard-checkout/api-integration/api-reference/create-payment
- Webhook: https://developer.phonepe.com/payment-gateway/website-integration/standard-checkout/api-integration/api-reference/webhook
- Order status: https://developer.phonepe.com/payment-gateway/website-integration/standard-checkout/api-integration/api-reference/order-status
- UAT sandbox: https://developer.phonepe.com/payment-gateway/uat-testing-go-live/uat-sandbox
- .NET SDK: https://developer.phonepe.com/payment-gateway/backend-sdk/net-backend-sdk/introduction
- Without GST: https://business.phonepe.com/articles/payment-gateway-without-gst-do-you-need-registration-to-accept-payments

**Other providers**
- Paytm pricing: https://www.paytmpayments.com/pricing
- Paytm 0% UPI blog: https://business.paytm.com/blog/payment-gateway-upi-mdr-charges/
- Zoho fees: https://www.zoho.com/in/payments/faq/general/transaction-fee
- PayU pricing: https://payu.in/pricing/
- Instamojo pricing: https://www.capterra.com/p/182477/Instamojo/pricing/

**Stripe and PayPal**
- Stripe invite-only in India: https://support.stripe.com/questions/stripe-accounts-are-invite-only-in-india
- Stripe India FAQ: https://support.stripe.com/questions/india-faq
- Stripe India pricing: https://stripe.com/in/pricing
- PayPal domestic India (third-party): https://onlinesellingindia.com/paypal-domestic-payments-in-india/

**Open source**
- Hyperswitch: https://github.com/juspay/hyperswitch
- BTCPay FAQ: https://docs.btcpayserver.org/FAQ/General/
- Kill Bill: https://github.com/killbill/killbill
- Lago licence: https://getlago.com/blog/open-source-licensing-and-why-lago-chose-agplv3
- Medusa: https://github.com/medusajs/medusa

**UPI and NPCI**
- Linking spec: https://www.labnol.org/files/linking.pdf
- Collect-request discontinuation: https://www.angelone.in/news/market-updates/npci-to-end-upi-person-to-person-collect-requests-from-october-to-curb-fraud
- Beneficiary name display: https://www.npci.org.in/uploads/UPI_OC_No_101_A_FY_2025_26_Strengthening_beneficiary_name_verification_and_display_during_UPI_transactions_eb7bd7ed72.pdf
- Small-merchant limits: https://taxguru.in/finance/implementation-maximum-upi-credit-limits-p2pm-merchants.html
- Google Pay iOS: https://developers.google.com/pay/india/api/ios/in-app-payments

**MDR**
- Inc42: https://inc42.com/buzz/parliament-clears-taxation-bill-fm-re-assures-no-mdr-on-p2p-upi-payments/
- PRS bill track: https://prsindia.org/billtrack/the-taxation-and-other-laws-amendment-bill-2026
- BusinessToday: https://www.businesstoday.in/latest/economy/story/heres-how-much-mdr-could-be-charged-on-upi-and-rupay-transactions-548523-2026-08-11

**Regulation**
- PA Directions 2025 (TaxGuru): https://taxguru.in/rbi/rbi-regulation-payment-aggregators-directions-2025.html
- PA Directions analysis (IndiaCorpLaw): https://indiacorplaw.in/2025/10/09/decoding-rbis-overhaul-of-the-payment-aggregator-directions/
- PPI Master Direction (Lexology): https://www.lexology.com/library/detail.aspx?g=8830a2f4-c7ef-4fe0-8966-5a59be808d52
- PPI Master Direction (Trilegal): https://trilegal.com/knowledge_repository/new-master-directions-on-prepaid-payment-instruments/
- GST TCS: https://taxgarden.in/blog/gst-on-ecommerce-operators-tcs-section-52-india-2026
- UPI account freezes: https://www.outlookmoney.com/banking/when-upi-transactions-lead-to-19-month-debit-freeze-what-should-you-do
