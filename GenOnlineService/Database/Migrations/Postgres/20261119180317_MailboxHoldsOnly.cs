using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenOnlineService.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class MailboxHoldsOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_users_email_normalized",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ux_identifier_hashes_kind_value",
                table: "identifier_hashes");

            migrationBuilder.CreateIndex(
                name: "ix_users_email_normalized",
                table: "users",
                column: "email_normalized");

            migrationBuilder.CreateIndex(
                name: "ix_identifier_hashes_kind_value",
                table: "identifier_hashes",
                columns: new[] { "kind", "value_hmac" });

            // a live account's mailbox is no identity: only the holds a ban keeps stay
            migrationBuilder.Sql("DELETE FROM identifier_hashes WHERE sanction_id IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_users_email_normalized",
                table: "users");

            migrationBuilder.DropIndex(
                name: "ix_identifier_hashes_kind_value",
                table: "identifier_hashes");

            migrationBuilder.CreateIndex(
                name: "ux_users_email_normalized",
                table: "users",
                column: "email_normalized",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_identifier_hashes_kind_value",
                table: "identifier_hashes",
                columns: new[] { "kind", "value_hmac" },
                unique: true);
        }
    }
}
