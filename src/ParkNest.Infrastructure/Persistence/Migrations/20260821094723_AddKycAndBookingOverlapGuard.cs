using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParkNest.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddKycAndBookingOverlapGuard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "kyc_submissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LegalName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DocumentType = table.Column<int>(type: "integer", nullable: false),
                    DocumentLast4 = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    DocumentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DocumentPhotoUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PayoutAccountLast4 = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReviewedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_kyc_submissions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_kyc_submissions_DocumentHash",
                table: "kyc_submissions",
                column: "DocumentHash");

            migrationBuilder.CreateIndex(
                name: "IX_kyc_submissions_Status_SubmittedAt",
                table: "kyc_submissions",
                columns: new[] { "Status", "SubmittedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_kyc_submissions_UserId_SubmittedAt",
                table: "kyc_submissions",
                columns: new[] { "UserId", "SubmittedAt" });

            // Double-booking, settled by the database rather than by a check that reads and then
            // writes.
            //
            // BookingService still asks first, because "already booked for part of that window"
            // is a better answer than a constraint name. But two requests can pass that check in
            // the same instant and both insert — and the loser of that race has a renter driving
            // to a bay that is taken, with credits held against it. An exclusion constraint is the
            // only place the answer can be final.
            //
            // Scoped to Held and Active: a cancelled or finished booking occupies nothing, and
            // including them would make a space unbookable forever after its first session.
            //
            // On a database that already holds overlapping live bookings this ALTER fails, which
            // is the correct outcome — it means the race has already happened and somebody has to
            // decide which of those two renters gets the bay.
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist;");

            migrationBuilder.Sql("""
                ALTER TABLE bookings
                ADD CONSTRAINT bookings_no_overlap
                EXCLUDE USING GIST (
                    "ParkingSpaceId" WITH =,
                    tstzrange("StartTime", "ExpectedEndTime", '[)') WITH &&
                )
                WHERE ("Status" IN (0, 1));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE bookings DROP CONSTRAINT IF EXISTS bookings_no_overlap;");

            migrationBuilder.DropTable(
                name: "kyc_submissions");
        }
    }
}
