using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Integration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWebhookDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No `xmin` column on whk_delivery although the model maps one: it is PostgreSQL's
            // system column, present on every table, and creating it fails with « column name
            // "xmin" conflicts with a system column name » (see AddRelayAgentRegistry).
            migrationBuilder.CreateTable(
                name: "whk_delivery",
                schema: "integration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    lead_id = table.Column<Guid>(type: "uuid", nullable: true),
                    client_id = table.Column<Guid>(type: "uuid", nullable: true),
                    body = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_status_code = table.Column<int>(type: "integer", nullable: true),
                    last_failure_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    last_duration_ms = table.Column<int>(type: "integer", nullable: true),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_whk_delivery", x => x.id);
                    table.ForeignKey(
                        name: "fk_whk_delivery_webhook_subscriptions_subscription_id",
                        column: x => x.subscription_id,
                        principalSchema: "integration",
                        principalTable: "whk_subscription",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_whk_delivery_status_next_attempt_at",
                schema: "integration",
                table: "whk_delivery",
                columns: new[] { "status", "next_attempt_at" });

            migrationBuilder.CreateIndex(
                name: "ix_whk_delivery_subscription_id_created_at",
                schema: "integration",
                table: "whk_delivery",
                columns: new[] { "subscription_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_whk_delivery_subscription_id_event_id",
                schema: "integration",
                table: "whk_delivery",
                columns: new[] { "subscription_id", "event_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_whk_delivery_tenant_id_client_id",
                schema: "integration",
                table: "whk_delivery",
                columns: new[] { "tenant_id", "client_id" });

            migrationBuilder.CreateIndex(
                name: "ix_whk_delivery_tenant_id_lead_id",
                schema: "integration",
                table: "whk_delivery",
                columns: new[] { "tenant_id", "lead_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "whk_delivery",
                schema: "integration");
        }
    }
}
