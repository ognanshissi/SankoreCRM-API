using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Administration.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAgencyManager : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "manager_user_id",
                schema: "administration",
                table: "agencies",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_agencies_tenant_id_manager_user_id",
                schema: "administration",
                table: "agencies",
                columns: new[] { "tenant_id", "manager_user_id" },
                filter: "manager_user_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_agencies_tenant_id_manager_user_id",
                schema: "administration",
                table: "agencies");

            migrationBuilder.DropColumn(
                name: "manager_user_id",
                schema: "administration",
                table: "agencies");
        }
    }
}
