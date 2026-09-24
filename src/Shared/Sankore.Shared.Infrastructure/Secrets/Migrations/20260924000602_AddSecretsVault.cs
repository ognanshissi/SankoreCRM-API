using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Shared.Infrastructure.Secrets.Migrations
{
    /// <inheritdoc />
    public partial class AddSecretsVault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "secrets");

            migrationBuilder.CreateTable(
                name: "entries",
                schema: "secrets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    encrypted_value = table.Column<string>(type: "text", nullable: false),
                    iv = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    tag = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_entries", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_secrets_tenant_scope_entity_name",
                schema: "secrets",
                table: "entries",
                columns: new[] { "tenant_id", "scope", "entity_id", "name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "entries",
                schema: "secrets");
        }
    }
}
