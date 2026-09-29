using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParkNest.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Launch in Gurgaon only (see <c>Cities.Supported</c>). Price bands for any other city are
    /// removed — no listing there can exist any more — and Gurgaon gets a city-wide band per
    /// vehicle type if it has none, so a freshly wiped database can publish without an admin step
    /// first. The admin edits these on the Pricing page like any other band.
    /// </summary>
    public partial class GurgaonOnlyLaunch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM city_pricing_configs WHERE lower("City") <> 'gurgaon';
                """);

            // VehicleType: 1 = TwoWheeler, 2 = FourWheeler.
            migrationBuilder.Sql("""
                INSERT INTO city_pricing_configs
                    ("Id", "City", "Zone", "VehicleType", "MinPricePerHour", "MaxPricePerHour", "OverstayMultiplier", "IsActive", "UpdatedAt")
                SELECT md5(random()::text || clock_timestamp()::text || v.type)::uuid, 'Gurgaon', NULL, v.type, v.min, v.max, 1.5, TRUE, now()
                FROM (VALUES (2, 20.00, 150.00), (1, 10.00, 60.00)) AS v(type, min, max)
                WHERE NOT EXISTS (
                    SELECT 1 FROM city_pricing_configs c
                    WHERE lower(c."City") = 'gurgaon' AND c."Zone" IS NULL AND c."VehicleType" = v.type);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: the removed bands are not recoverable, and the Gurgaon ones are data
            // the admin may since have edited.
        }
    }
}
