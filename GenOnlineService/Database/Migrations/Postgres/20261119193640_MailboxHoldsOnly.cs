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
                name: "ux_identifier_hashes_kind_value",
                table: "identifier_hashes");

            migrationBuilder.CreateIndex(
                name: "ix_identifier_hashes_kind_value",
                table: "identifier_hashes",
                columns: new[] { "kind", "value_hmac" });

            // a live account keeps no hash of its address any more (the address is held by users.email_normalized): only the
            // holds a ban keeps stay. The holds of erased banned accounts were keyed on a mailbox form and are left to expire.
            migrationBuilder.Sql("DELETE FROM identifier_hashes WHERE sanction_id IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_identifier_hashes_kind_value",
                table: "identifier_hashes");

            migrationBuilder.CreateIndex(
                name: "ux_identifier_hashes_kind_value",
                table: "identifier_hashes",
                columns: new[] { "kind", "value_hmac" },
                unique: true);
        }
    }
}
