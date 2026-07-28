# ADR 0003 — Geo-search via raw SQL, not NetTopologySuite in the domain

**Date:** 2026-07-28
**Status:** Accepted

## Context

PRD §14.1 specifies PostgreSQL + PostGIS for "spaces near me". The idiomatic EF Core approach is
`Npgsql.EntityFrameworkCore.PostgreSQL.NetTopologySuite` with a `Point Location` property on
`ParkingSpace`.

That property type is fixed at the entity level, and no other EF provider understands it. Since a
`ParkingSpace` is needed to construct almost any booking test, adopting it would force the entire
test suite onto a real PostGIS container.

## Decision

`ParkingSpace` stores plain `Latitude`/`Longitude` doubles. A Postgres **generated column**
derives `geog geography(Point,4326)` from them, indexed with GiST. Geo-queries run as
parameterised raw SQL in `PostgresSpaceSearchService`, behind the `ISpaceSearchService` interface.

## Why

- The domain model stays provider-agnostic, so the test suite runs on in-memory SQLite: 26 tests
  in about a second, no Docker, no container lifecycle in CI.
- The generated column cannot drift from lat/lng. There is no code path that updates one and
  forgets the other, which is the usual failure mode of a manually maintained geometry column.
- PostGIS is still doing the real work — `ST_DWithin` against a GiST index, which is what the
  <300ms target in PRD §15 depends on. Nothing is given up on the query side.

## Consequences

- Geo-search is not covered by the SQLite test suite. It needs either an integration test against
  a real PostGIS container or manual verification. **This is a real gap** — the query is currently
  unverified by any automated test.
- Raw SQL means column names are stringly-typed against the EF model. A rename in
  `ParkingSpace` breaks the query at runtime, not compile time. Worth an integration test purely
  as a canary.
- Richer spatial work later (polygon zones, routing) would want NTS after all. At that point,
  reconsider — with the search behind an interface, swapping the implementation is contained.
