#!/usr/bin/env bash
#
# Proves the hand-written migration actually took effect. The SQLite unit-test suite cannot see
# any of this: the PostGIS geography column, the GiST index, the non-negative CHECK constraints,
# or the trigger that makes ledger_entries append-only.
#
# Run against a live Postgres by CI (see .github/workflows/ci.yml).

set -euo pipefail

PSQL="psql -h localhost -U parknest -d parknest -tAX"

fail() {
    echo "FAIL: $1" >&2
    exit 1
}

check_exists() {
    local description="$1" query="$2"
    local result
    result=$($PSQL -c "$query")
    if [[ "$result" != "1" ]]; then
        fail "$description"
    fi
    echo "  ok: $description"
}

echo "Verifying schema objects the unit tests cannot reach..."

check_exists "postgis extension installed" \
    "SELECT 1 FROM pg_extension WHERE extname = 'postgis';"

check_exists "parking_spaces.geog generated geography column" \
    "SELECT 1 FROM information_schema.columns
     WHERE table_name = 'parking_spaces' AND column_name = 'geog';"

check_exists "GiST index on parking_spaces.geog" \
    "SELECT 1 FROM pg_indexes
     WHERE tablename = 'parking_spaces' AND indexname = 'ix_parking_spaces_geog';"

for constraint in \
    ck_wallets_spendable_non_negative \
    ck_wallets_held_non_negative \
    ck_wallets_earning_non_negative \
    ck_ledger_entries_amount_positive
do
    check_exists "constraint $constraint" \
        "SELECT 1 FROM pg_constraint WHERE conname = '$constraint';"
done

check_exists "append-only trigger on ledger_entries" \
    "SELECT 1 FROM pg_trigger WHERE tgname = 'trg_ledger_entries_append_only';"

echo
echo "Verifying the invariants actually bite..."

# A negative balance must be rejected by the CHECK constraint, not merely discouraged in C#.
if $PSQL -c "INSERT INTO wallets (\"Id\", \"UserId\", \"SpendableBalance\", \"HeldBalance\", \"EarningBalance\", \"Version\", \"CreatedAt\")
             VALUES (gen_random_uuid(), gen_random_uuid(), -1, 0, 0, 0, now());" >/dev/null 2>&1
then
    fail "a negative SpendableBalance was accepted — the CHECK constraint is not working"
fi
echo "  ok: negative wallet balance rejected"

# And an UPDATE against the ledger must be refused outright.
$PSQL -c "INSERT INTO ledger_transactions (\"Id\", \"Type\", \"IdempotencyKey\", \"CreatedAt\")
          VALUES ('11111111-1111-1111-1111-111111111111', 1, 'schema-verify', now());" >/dev/null
$PSQL -c "INSERT INTO ledger_entries (\"Id\", \"LedgerTransactionId\", \"AccountType\", \"Direction\", \"Amount\", \"CreatedAt\")
          VALUES ('22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111', 11, 1, 100, now());" >/dev/null

if $PSQL -c "UPDATE ledger_entries SET \"Amount\" = 999
             WHERE \"Id\" = '22222222-2222-2222-2222-222222222222';" >/dev/null 2>&1
then
    fail "a ledger entry was updated — the append-only trigger is not working"
fi
echo "  ok: ledger entry UPDATE rejected"

if $PSQL -c "DELETE FROM ledger_entries
             WHERE \"Id\" = '22222222-2222-2222-2222-222222222222';" >/dev/null 2>&1
then
    fail "a ledger entry was deleted — the append-only trigger is not working"
fi
echo "  ok: ledger entry DELETE rejected"

echo
echo "Schema verification passed."
