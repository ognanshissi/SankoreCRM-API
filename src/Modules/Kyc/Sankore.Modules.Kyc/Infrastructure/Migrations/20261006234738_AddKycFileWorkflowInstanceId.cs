using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sankore.Modules.Kyc.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddKycFileWorkflowInstanceId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "workflow_instance_id",
                schema: "kyc",
                table: "kyc_files",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "workflow_instance_id",
                schema: "kyc",
                table: "kyc_files");
        }
    }
}
