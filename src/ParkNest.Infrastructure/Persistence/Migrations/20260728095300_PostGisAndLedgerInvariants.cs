using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParkNest.Infrastructure.Persistence.Migrations;

/// <summary>
/// Everything the EF model cannot express: the PostGIS geography column that powers geo-search,
/// and the CHECK constraints that make an invalid ledger physically unrepresentable.
/// Hand-written because it is pure SQL — it does not change the EF model snapshot.
/// </summary>
[Migration("20260728095300_PostGisAndLedgerInvariants")]
public partial class PostGisAndLedgerInvariants : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS postgis;");

        // A generated column keeps the geometry in lockstep with lat/lng automatically — there is
        // no code path that can update one and forget the other.
        migrationBuilder.Sql("""
            ALTER TABLE parking_spaces
            ADD COLUMN geog geography(Point, 4326)
            GENERATED ALWAYS AS (
                ST_SetSRID(ST_MakePoint("Longitude", "Latitude"), 4326)::geography
            ) STORED;
            """);

        // GiST index: the difference between a 300ms "spaces near me" and a full table scan.
        migrationBuilder.Sql("CREATE INDEX ix_parking_spaces_geog ON parking_spaces USING GIST (geog);");

        // Ledger invariants. These are the last line of defence: even a bug in LedgerService, or a
        // hand-run UPDATE during an incident, cannot leave a wallet negative or an amount signed.
        migrationBuilder.Sql("""
            ALTER TABLE wallets
                ADD CONSTRAINT ck_wallets_spendable_non_negative CHECK ("SpendableBalance" >= 0),
                ADD CONSTRAINT ck_wallets_held_non_negative      CHECK ("HeldBalance" >= 0),
                ADD CONSTRAINT ck_wallets_earning_non_negative   CHECK ("EarningBalance" >= 0);
            """);

        migrationBuilder.Sql("""
            ALTER TABLE ledger_entries
                ADD CONSTRAINT ck_ledger_entries_amount_positive CHECK ("Amount" > 0);
            """);

        // Ledger entries are append-only: no UPDATE, no DELETE, ever. Corrections are made by
        // posting a compensating transaction (PRD §5.1.7).
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION parknest_reject_ledger_mutation()
            RETURNS trigger AS $$
            BEGIN
                RAISE EXCEPTION 'ledger entries are append-only; post a compensating transaction instead';
            END;
            $$ LANGUAGE plpgsql;
            """);

        migrationBuilder.Sql("""
            CREATE TRIGGER trg_ledger_entries_append_only
            BEFORE UPDATE OR DELETE ON ledger_entries
            FOR EACH ROW EXECUTE FUNCTION parknest_reject_ledger_mutation();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_ledger_entries_append_only ON ledger_entries;");
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS parknest_reject_ledger_mutation();");
        migrationBuilder.Sql("ALTER TABLE ledger_entries DROP CONSTRAINT IF EXISTS ck_ledger_entries_amount_positive;");
        migrationBuilder.Sql("""
            ALTER TABLE wallets
                DROP CONSTRAINT IF EXISTS ck_wallets_spendable_non_negative,
                DROP CONSTRAINT IF EXISTS ck_wallets_held_non_negative,
                DROP CONSTRAINT IF EXISTS ck_wallets_earning_non_negative;
            """);
        migrationBuilder.Sql("DROP INDEX IF EXISTS ix_parking_spaces_geog;");
        migrationBuilder.Sql("ALTER TABLE parking_spaces DROP COLUMN IF EXISTS geog;");
    }
}
