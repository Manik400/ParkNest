using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParkNest.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentPhone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PaymentPhone",
                table: "users",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PaymentPhone",
                table: "users");
        }
    }
}
