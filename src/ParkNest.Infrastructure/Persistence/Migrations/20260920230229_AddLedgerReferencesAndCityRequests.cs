using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParkNest.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLedgerReferencesAndCityRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LedgerTransactionId",
                table: "payouts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Reference",
                table: "ledger_transactions",
                type: "character varying(14)",
                maxLength: 14,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "RevertsTransactionId",
                table: "ledger_transactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "city_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    City = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Note = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_city_requests", x => x.Id);
                });

            // Rows written before references existed get one now, derived from the id so the
            // same row always gets the same reference. The unique index below would otherwise
            // refuse a table full of empty strings. Restricted to the reference alphabet's
            // shape well enough for a backfill: hex from the uuid, upper-cased.
            migrationBuilder.Sql(
                """
                UPDATE ledger_transactions
                SET "Reference" = 'TXN-' || upper(substr(replace("Id"::text, '-', ''), 1, 10))
                WHERE "Reference" = '';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_ledger_transactions_Reference",
                table: "ledger_transactions",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ledger_transactions_RevertsTransactionId",
                table: "ledger_transactions",
                column: "RevertsTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_city_requests_UserId_City",
                table: "city_requests",
                columns: new[] { "UserId", "City" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "city_requests");

            migrationBuilder.DropIndex(
                name: "IX_ledger_transactions_Reference",
                table: "ledger_transactions");

            migrationBuilder.DropIndex(
                name: "IX_ledger_transactions_RevertsTransactionId",
                table: "ledger_transactions");

            migrationBuilder.DropColumn(
                name: "LedgerTransactionId",
                table: "payouts");

            migrationBuilder.DropColumn(
                name: "Reference",
                table: "ledger_transactions");

            migrationBuilder.DropColumn(
                name: "RevertsTransactionId",
                table: "ledger_transactions");
        }
    }
}
