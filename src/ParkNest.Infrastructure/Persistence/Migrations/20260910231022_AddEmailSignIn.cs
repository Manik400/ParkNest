using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ParkNest.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Sign-in by email as well as phone. An account now starts with whichever contact the user
    /// signed in with, so users.Phone becomes nullable and users.Email gains a unique index; an
    /// OTP row's Phone becomes Destination, wide enough for an address.
    ///
    /// Hand-edited from the scaffold: EF proposed dropping otp_codes.Phone and adding Destination,
    /// which would throw away every code in flight. A rename keeps them verifiable.
    /// </summary>
    public partial class AddEmailSignIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Phone",
                table: "otp_codes",
                newName: "Destination");

            migrationBuilder.RenameIndex(
                name: "IX_otp_codes_Phone_ConsumedAt_CreatedAt",
                table: "otp_codes",
                newName: "IX_otp_codes_Destination_ConsumedAt_CreatedAt");

            migrationBuilder.AlterColumn<string>(
                name: "Destination",
                table: "otp_codes",
                type: "character varying(254)",
                maxLength: 254,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AlterColumn<string>(
                name: "Phone",
                table: "users",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20);

            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "users",
                type: "character varying(254)",
                maxLength: 254,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200,
                oldNullable: true);

            // Email was never written before this migration, so there is nothing to collide.
            migrationBuilder.CreateIndex(
                name: "IX_users_Email",
                table: "users",
                column: "Email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_users_Email",
                table: "users");

            // Codes sent to an address do not fit back into a 20-character phone column.
            migrationBuilder.Sql("DELETE FROM otp_codes WHERE \"Destination\" LIKE '%@%';");

            // Nor does an account with no phone fit a NOT NULL one. Refusing is safer than
            // inventing numbers or deleting accounts that hold money.
            migrationBuilder.Sql(
                "DO $$ BEGIN IF EXISTS (SELECT 1 FROM users WHERE \"Phone\" IS NULL) THEN " +
                "RAISE EXCEPTION 'Cannot roll back AddEmailSignIn: some accounts exist only by email.'; " +
                "END IF; END $$;");

            migrationBuilder.AlterColumn<string>(
                name: "Phone",
                table: "users",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Email",
                table: "users",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(254)",
                oldMaxLength: 254,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Destination",
                table: "otp_codes",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(254)",
                oldMaxLength: 254);

            migrationBuilder.RenameIndex(
                name: "IX_otp_codes_Destination_ConsumedAt_CreatedAt",
                table: "otp_codes",
                newName: "IX_otp_codes_Phone_ConsumedAt_CreatedAt");

            migrationBuilder.RenameColumn(
                name: "Destination",
                table: "otp_codes",
                newName: "Phone");
        }
    }
}
