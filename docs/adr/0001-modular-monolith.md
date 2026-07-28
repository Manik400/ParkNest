# ADR 0001 — Start as a modular monolith, not microservices

**Date:** 2026-07-28
**Status:** Accepted

## Context

PRD §11 draws eight services behind an API gateway with RabbitMQ between them. PRD §18 then says
Phase 0 should be a modular monolith. Both are in the spec; the code has to pick one.

## Decision

Build one deployable ASP.NET Core application with enforced internal module boundaries. The
project layout mirrors the service decomposition exactly, so each future service already has its
namespace and its own set of entities.

## Why

Microservices buy independent deployment and independent scaling. Neither is worth anything before
there is traffic, and both cost a great deal up front: distributed transactions across
Booking→Wallet, per-service migrations, local dev requiring eight containers, and tracing to
debug anything.

The ledger in particular argues *against* early splitting. Hold, overstay debit and settlement
want to be one database transaction. Splitting Booking from Wallet on day one means introducing
sagas and compensating transactions for a correctness problem that a single `BEGIN`/`COMMIT`
solves for free.

## Consequences

- Layers enforce direction (`Domain` ← `Application` ← `Infrastructure` ← `Api`), but nothing
  stops one module calling another's internals. That discipline is currently by convention only;
  if it starts slipping, add an architecture test.
- The RabbitMQ decoupling in PRD §18 Phase 1 becomes a refactor of in-process calls to published
  events, not a rewrite.
- Scaling is all-or-nothing until a split happens. Acceptable at Phase 0 volumes.

## When to revisit

When any one of these is true: the ledger's write throughput becomes the bottleneck; two parts of
the system need genuinely different deploy cadences; or the team grows past the point where one
codebase is comfortable.
