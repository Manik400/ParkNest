using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParkNest.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPricingBandChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pricing_band_changes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CityPricingConfigId = table.Column<Guid>(type: "uuid", nullable: false),
                    City = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Zone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    VehicleType = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    PreviousMinPricePerHour = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    PreviousMaxPricePerHour = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    PreviousOverstayMultiplier = table.Column<decimal>(type: "numeric(6,3)", precision: 6, scale: 3, nullable: true),
                    PreviousIsActive = table.Column<bool>(type: "boolean", nullable: true),
                    MinPricePerHour = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    MaxPricePerHour = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    OverstayMultiplier = table.Column<decimal>(type: "numeric(6,3)", precision: 6, scale: 3, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ChangedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pricing_band_changes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_pricing_band_changes_ChangedAt",
                table: "pricing_band_changes",
                column: "ChangedAt");

            migrationBuilder.CreateIndex(
                name: "IX_pricing_band_changes_CityPricingConfigId_ChangedAt",
                table: "pricing_band_changes",
                columns: new[] { "CityPricingConfigId", "ChangedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pricing_band_changes");
        }
    }
}
