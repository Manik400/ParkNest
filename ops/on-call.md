# On call

Alertmanager routes. This is the half it cannot: who receives the page, and what they do about it.

Two of the three names below are blank on purpose. A rota with a placeholder in it is worse than
an empty one — an empty one is obviously unfinished, a placeholder looks answered. Fill them in
before the first deployment that handles real money, and treat that as a release blocker rather
than paperwork.

## The rota

| Role | Who | Reachable on |
|---|---|---|
| Primary | _unassigned_ | _unassigned_ |
| Secondary — paged when the primary does not acknowledge in 15 minutes | _unassigned_ | _unassigned_ |
| Ledger escalation — the only person who may post a correcting transaction | _unassigned_ | _unassigned_ |

Rotation is weekly, handing over Monday morning. The handover is a conversation, not a calendar
event: whatever fired last week and was not resolved is the first thing the incoming primary
hears about.

**One person can hold primary and secondary only if nobody else exists yet.** Say so here when
that is the case, rather than leaving the secondary row looking staffed.

## Wiring it up

`alertmanager/alertmanager.yml` routes everything to a local sink that prints what it receives.
That is the right default — it demonstrably works, and it never fails silently at the far end.
To route somewhere real:

1. Create the receiver (Slack webhook, PagerDuty routing key, an email relay — the file has a
   commented Slack block to copy).
2. Put the credential in `ops/alertmanager/.secret.yml` or an environment variable. Not in the
   committed file.
3. Point the `severity="critical"` route at it. Leave the warnings on the sink until somebody has
   agreed to read them; an alert with no reader is a rule that should be deleted, not routed.
4. Fire a test alert and confirm it lands, in the channel, on the phone of the person named above.

An untested delivery path is not a delivery path. Re-test it whenever the receiver changes.

## What each alert means, and what to do

### `LedgerDrift` — critical, page immediately

`parknest_reconciliation_drift_credits > 0`. Stored wallet balances and the ledger disagree about
somebody's money. Every other alert here can be explained by a quiet day; this one cannot.

1. **Do not correct a balance by hand.** The ledger is the truth and the balance is a projection
   of it; editing the projection hides the bug that produced the drift and loses the evidence.
2. Find the affected wallets — the sweep logs them, and `/api/admin` ledger trails show the
   entries behind each one.
3. If drift is growing, stop the writes rather than chase them: take the API out of rotation.
   A ledger that is wrong and still moving is worse than a platform that is briefly down.
4. Escalate to the ledger escalation contact. Only they post a correcting transaction, and it is
   posted as a compensating entry — never as an edit to an existing one. See
   [docs/ledger-model.md](../docs/ledger-model.md).

### `HighShortfallRate` — warning

Renters are running out of credits mid-session more often than usual. Suppressed while
`LedgerDrift` is firing, because it is usually the same incident from a second angle.

Genuine causes worth separating: a pricing band raised too far, the overstay meter billing
against a wrong rate, or a recharge path failing so balances are not topping up. Check whether
payment orders are completing before assuming it is behaviour.

### `HighDisputeRate` — warning

Disputes opened per settled booking is up. Almost never an infrastructure problem — look for a
host with a mis-described space, or a check-in flow failing in one city and renters being billed
for sessions they could not start.

## What is not an alert

A page that nobody acts on trains people to ignore pages. These are deliberately not routed:

- API latency, unless it breaches the PRD §15 budget for long enough to matter to a user standing
  next to a car.
- A single failed payout. It refunds itself to the host's earning balance and an operator picks
  it up from the console.
- Push delivery failures. `Push:Provider=None` is a supported production state — the durable
  notification is written either way, so a Firebase outage costs a buzz and nothing else.
