#!/usr/bin/env bash
#
# Static guards on money code. These encode rules from docs/ledger-model.md that the compiler
# cannot enforce — the kind of thing that is obvious in review right up until the one time it
# isn't. Each check explains the rule it protects when it fails.
#
# Run by .github/workflows/ledger-guard.yml, which only triggers when money paths change.
# Runnable locally: BASE_REF=main bash .github/scripts/ledger-guard.sh

set -uo pipefail

BASE_REF="${BASE_REF:-main}"
FAILED=0

fail() {
    echo
    echo "✗ FAIL: $1"
    echo
    FAILED=1
}

pass() {
    echo "✓ $1"
}

echo "Ledger guard — base branch: ${BASE_REF}"
echo

# ---------------------------------------------------------------------------
# 1. Only Wallet.Apply may assign a balance.
#
# The whole double-entry design rests on LedgerService being the single writer. A stray
# `wallet.SpendableBalance = x` elsewhere bypasses the balance check, the entry stream and the
# audit trail all at once, and the stored balance silently stops matching the ledger.
# ---------------------------------------------------------------------------
offenders=$(grep -rnE '(SpendableBalance|HeldBalance|EarningBalance)[[:space:]]*(\+=|-=|=[^=])' \
    --include='*.cs' src/ 2>/dev/null \
    | grep -v 'src/ParkNest.Domain/Wallets/Wallet.cs' \
    | grep -v '/Migrations/' \
    || true)

if [[ -n "$offenders" ]]; then
    fail "wallet balance assigned outside Wallet.Apply"
    echo "$offenders"
    echo
    echo "  Balances may only change through LedgerService.PostAsync, which writes the matching"
    echo "  double-entry legs. Assigning directly bypasses the ledger and breaks reconciliation."
    echo "  See docs/ledger-model.md."
else
    pass "no wallet balance assigned outside Wallet.Apply"
fi

# ---------------------------------------------------------------------------
# 2. Applied migrations are immutable.
#
# Editing a migration that has already run means dev, CI and production silently diverge:
# the file says one thing, the deployed database says another, and __EFMigrationsHistory
# claims everything is fine.
# ---------------------------------------------------------------------------
modified_migrations=$(git diff --diff-filter=M --name-only "origin/${BASE_REF}...HEAD" \
    -- 'src/ParkNest.Infrastructure/Persistence/Migrations/' 2>/dev/null \
    | grep -v 'ModelSnapshot' \
    || true)

if [[ -n "$modified_migrations" ]]; then
    fail "an existing migration was modified"
    echo "$modified_migrations"
    echo
    echo "  Migrations are append-only once merged. Any database that already applied this"
    echo "  migration will never re-run it, so the edit reaches no existing environment."
    echo "  Add a new migration instead."
else
    pass "no existing migration modified"
fi

# ---------------------------------------------------------------------------
# 2b. Every migration must carry its Designer metadata.
#
# EF associates a migration with its DbContext through the [DbContext] attribute, which lives in
# the generated .Designer.cs. A hand-written migration without one is silently *skipped* by
# `database update` — it looks committed and reviewed, and never runs. That exact bug shipped the
# PostGIS column and all the ledger CHECK constraints into a state where they existed in the repo
# but in no database.
# ---------------------------------------------------------------------------
orphans=""
for migration in src/ParkNest.Infrastructure/Persistence/Migrations/*.cs; do
    case "$migration" in
        *.Designer.cs|*ModelSnapshot.cs) continue ;;
    esac
    [[ -e "$migration" ]] || continue

    designer="${migration%.cs}.Designer.cs"
    if [[ ! -f "$designer" ]]; then
        orphans+="  ${migration}"$'\n'
    fi
done

if [[ -n "$orphans" ]]; then
    fail "a migration has no .Designer.cs and will be silently skipped by EF"
    echo "$orphans"
    echo "  Scaffold migrations with 'dotnet ef migrations add', then edit the generated Up/Down."
    echo "  Never hand-write the migration file — without the Designer's [DbContext] attribute EF"
    echo "  cannot see it, and 'database update' will report success while applying nothing."
else
    pass "every migration has its Designer metadata"
fi

# ---------------------------------------------------------------------------
# 3. Every new wallet-mutating method takes an idempotency key.
#
# PRD §15 requires idempotency on wallet mutations: a renter's phone losing signal mid-checkout
# must not double-debit them.
# ---------------------------------------------------------------------------
missing_idempotency=$(grep -nE 'Task<[^>]*>[[:space:]]+(PostAsync|.*Async)\(' \
    src/ParkNest.Application/Wallets/IWalletService.cs 2>/dev/null \
    | grep -viE 'idempotencyKey|GetOrCreateWalletAsync|SettleAsync|RecomputeFromEntriesAsync' \
    || true)

if [[ -n "$missing_idempotency" ]]; then
    fail "a wallet operation is missing an idempotency key"
    echo "$missing_idempotency"
    echo
    echo "  Every method that moves credits needs a caller-supplied idempotency key so a retried"
    echo "  request replays instead of moving credits twice. (SettleAsync carries its key on the"
    echo "  SettlementRequest record.)"
else
    pass "all wallet operations carry an idempotency key"
fi

# ---------------------------------------------------------------------------
# 4. Ledger entries are never deleted or bulk-updated from application code.
#
# Corrections are compensating transactions. The Postgres trigger blocks this at runtime; catching
# it here turns a production exception into a failed check.
# ---------------------------------------------------------------------------
mutations=$(grep -rnE 'LedgerEntries\.(Remove|RemoveRange|ExecuteDelete|ExecuteUpdate)' \
    --include='*.cs' src/ 2>/dev/null || true)

if [[ -n "$mutations" ]]; then
    fail "application code deletes or bulk-updates ledger entries"
    echo "$mutations"
    echo
    echo "  The ledger is append-only. Correct a mistake by posting a compensating transaction so"
    echo "  the original error stays in the audit trail."
else
    pass "no deletion or bulk update of ledger entries"
fi

# ---------------------------------------------------------------------------
# 5. Ledger model docs stay in step with the account model.
#
# A new account type or transaction type changes the shape of the ledger. Warn (not fail) if the
# docs were not touched — sometimes the change genuinely does not need them.
# ---------------------------------------------------------------------------
enums_changed=$(git diff --name-only "origin/${BASE_REF}...HEAD" \
    -- 'src/ParkNest.Domain/Common/Enums.cs' 2>/dev/null || true)
docs_changed=$(git diff --name-only "origin/${BASE_REF}...HEAD" \
    -- 'docs/ledger-model.md' 2>/dev/null || true)

if [[ -n "$enums_changed" && -z "$docs_changed" ]]; then
    echo "⚠ WARN: Enums.cs changed but docs/ledger-model.md did not."
    echo "  If you added an account or transaction type, document its posting shape."
else
    pass "ledger documentation is in step"
fi

echo
if [[ $FAILED -ne 0 ]]; then
    echo "Ledger guard failed. These rules exist because the ledger is the one place a bug costs"
    echo "real money — see docs/ledger-model.md for the reasoning."
    exit 1
fi

echo "Ledger guard passed."
