# ADR 0005 — Phone + OTP auth with self-issued JWTs

**Date:** 2026-07-29
**Status:** Accepted

## Context

The Phase 0 scaffold shipped with no authentication at all. Worse than merely unprotected: the
endpoints took `RenterId` and `HostId` **from the request body**, so any caller could spend another
user's credits by typing their GUID. That is the single most serious defect in the codebase and it
blocks everything user-facing.

PRD §14.1 suggests Duende IdentityServer or Keycloak.

## Decision

Phone number + one-time code, exchanged for a self-issued HS256 JWT. No IdentityServer, no
Keycloak, no passwords.

Application services take the acting user from an injected `ICurrentUser` rather than from request
input. `CreateBookingRequest` and `CreateListingRequest` no longer *have* a user id field.

## Why

**Phone + OTP over passwords**: it is the login users in this market expect, it doubles as the
phone verification a marketplace needs anyway, and there are no password hashes, reset flows or
credential-stuffing exposure to manage.

**Self-issued JWT over Keycloak/Duende for now**: an external identity provider is another service
to run, configure and keep patched, and it earns its keep when there are multiple relying parties,
federation, or social login. There is one API and one app. The cost is real (no built-in refresh
tokens, no revocation list) and is accepted deliberately — see below.

**Removing the id from request DTOs rather than validating it**: a check that `body.RenterId ==
token.UserId` works, but only where someone remembered to write it. Deleting the field makes the
vulnerable code unrepresentable — there is no input to forget to validate.

## Consequences

- Every application service that acts on a user's behalf depends on `ICurrentUser`. Tests inject
  `TestCurrentUser` and switch the acting user with `SignIn`, which is how the authorisation tests
  verify that one user cannot touch another's booking or listing.
- Authorization fails closed: the API sets a `FallbackPolicy` requiring an authenticated user, so a
  new endpoint is protected unless it explicitly opts out with `[AllowAnonymous]`.
- `ForbiddenException` is deliberately separate from `DomainException` — a permission failure is
  403, not 400, and conflating them leaks resource existence through error messages.
- **No refresh tokens and no revocation.** A token is valid for its full 12 hours; there is no way
  to log a device out early. Acceptable at Phase 0, must be fixed before real users.
- **No rate limiting on OTP requests.** The attempt cap protects a given code, but nothing stops
  an attacker requesting thousands of codes and burning SMS budget. Must be fixed before launch.
- The dev OTP sender returns the code in the API response. `DependencyInjection` throws at startup
  in Production rather than allowing that to ship.

## Alternatives rejected

**Keycloak / Duende IdentityServer** — the right answer once there is a second client application,
B2B tenant SSO, or a compliance requirement for centralised identity. Revisit at Phase 2 when the
B2B pilot (PRD §18) lands, since housing societies and office parks are likely to want SSO.
