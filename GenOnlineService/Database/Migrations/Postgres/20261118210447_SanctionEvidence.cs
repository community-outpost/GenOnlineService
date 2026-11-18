using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace GenOnlineService.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class SanctionEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sanction_evidence",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    sanction_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false),
                    match_id = table.Column<long>(type: "bigint", nullable: false),
                    object_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_by_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sanction_evidence", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sanction_evidence_key",
                table: "sanction_evidence",
                column: "object_key");

            migrationBuilder.CreateIndex(
                name: "ux_sanction_evidence_sanction_key",
                table: "sanction_evidence",
                columns: new[] { "sanction_id", "object_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sanction_evidence");
        }
    }
}
