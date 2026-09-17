## What and why

<!-- What changes, and what problem it solves. Link the backlog item or PRD section. -->

## How to verify

<!-- The steps a reviewer runs to convince themselves it works. -->

## Checklist

- [ ] `dotnet test` passes locally
- [ ] New behaviour has tests
- [ ] Docs updated if architecture, the ledger model or the backlog changed

## If this touches money code

<!-- Delete this section if the PR does not touch Wallets, Bookings or migrations. -->

- [ ] All credit movement goes through `LedgerService.PostAsync`
- [ ] New transaction types are balanced and tested
- [ ] Wallet-mutating entry points take an idempotency key
- [ ] New migration added rather than an existing one edited

---

<sub>Comment `@claude` on this PR for an AI review. CI and the ledger guard run automatically.</sub>
