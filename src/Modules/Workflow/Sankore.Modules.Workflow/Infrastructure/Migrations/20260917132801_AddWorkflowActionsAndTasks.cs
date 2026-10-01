using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Workflow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowActionsAndTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // workflow_actions is CREATED here, which it never was.
            //
            // No migration in this module ever created it: the table existed only in the model
            // snapshot, having been made on a developer's database by a migration that was later
            // rewritten. This one was then scaffolded against that database and so begins by
            // DROPPING a foreign key, an index and a column on a table that, on any other database,
            // does not exist — which is why an empty database could not be provisioned: start-up
            // died here with 42P01 relation "workflow.workflow_actions" does not exist.
            //
            // Created in its POST-migration shape, taken from this migration's own Designer (and
            // unchanged by every migration after it). Not the pre-migration shape followed by the
            // three drops below: that would add workflow_instance_id only to remove it two
            // statements later. The drops are made conditional instead, so both worlds converge —
            // an existing database still loses the column, a fresh one never has it.
            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS workflow.workflow_actions (
                    id               uuid                   NOT NULL,
                    action_type      character varying(50)  NOT NULL,
                    config_json      text                   NOT NULL DEFAULT '{}',
                    execution_order  integer                NOT NULL DEFAULT 0,
                    template_id      uuid                   NOT NULL,
                    transition_id    uuid                   NOT NULL,
                    CONSTRAINT pk_workflow_actions PRIMARY KEY (id)
                );

                CREATE INDEX IF NOT EXISTS ix_workflow_actions_transition_id_execution_order
                    ON workflow.workflow_actions (transition_id, execution_order);
                """);

            // Conditional, for the reason above: on a fresh database the table was just created
            // without any of this, and an unguarded DROP would fail on every one of the three.
            migrationBuilder.Sql(
                """
                ALTER TABLE workflow.workflow_actions
                    DROP CONSTRAINT IF EXISTS fk_workflow_actions_workflow_instances_workflow_instance_id;

                DROP INDEX IF EXISTS workflow.ix_workflow_actions_workflow_instance_id;

                ALTER TABLE workflow.workflow_actions
                    DROP COLUMN IF EXISTS workflow_instance_id;
                """);

            migrationBuilder.CreateTable(
                name: "workflow_tasks",
                schema: "workflow",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    instance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    assigned_to_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assigned_role_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    priority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Normal"),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "Pending"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completion_comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workflow_tasks", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_tasks_tenant_id_assigned_to_user_id_status",
                schema: "workflow",
                table: "workflow_tasks",
                columns: new[] { "tenant_id", "assigned_to_user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_workflow_tasks_tenant_id_instance_id",
                schema: "workflow",
                table: "workflow_tasks",
                columns: new[] { "tenant_id", "instance_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // workflow_actions is deliberately NOT dropped here, even though Up may have created it.
            // Reverting past this point on an existing database must leave the table standing — it
            // predates this migration there, and dropping it would destroy rows this migration never
            // owned. On a fresh database it leaves an empty table behind, which is harmless.
            migrationBuilder.DropTable(
                name: "workflow_tasks",
                schema: "workflow");

            migrationBuilder.AddColumn<Guid>(
                name: "workflow_instance_id",
                schema: "workflow",
                table: "workflow_actions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_workflow_actions_workflow_instance_id",
                schema: "workflow",
                table: "workflow_actions",
                column: "workflow_instance_id");

            migrationBuilder.AddForeignKey(
                name: "fk_workflow_actions_workflow_instances_workflow_instance_id",
                schema: "workflow",
                table: "workflow_actions",
                column: "workflow_instance_id",
                principalSchema: "workflow",
                principalTable: "workflow_instances",
                principalColumn: "id");
        }
    }
}
