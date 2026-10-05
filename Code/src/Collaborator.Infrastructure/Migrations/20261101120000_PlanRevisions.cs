using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Collaborator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PlanRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Outbox_RunId_Kind",
                table: "Outbox");

            migrationBuilder.AddColumn<string>(
                name: "Decision",
                table: "Runs",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DecisionComment",
                table: "Runs",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Revision",
                table: "Outbox",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "PlanRevisions",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    PlanJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Decision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Comment = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    DecidedBy = table.Column<string>(type: "text", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanRevisions", x => new { x.RunId, x.Revision });
                    table.ForeignKey(
                        name: "FK_PlanRevisions_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_RunId_Kind_Revision",
                table: "Outbox",
                columns: new[] { "RunId", "Kind", "Revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlanRevisions");

            migrationBuilder.DropIndex(
                name: "IX_Outbox_RunId_Kind_Revision",
                table: "Outbox");

            migrationBuilder.DropColumn(
                name: "Decision",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "DecisionComment",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "Revision",
                table: "Outbox");

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_RunId_Kind",
                table: "Outbox",
                columns: new[] { "RunId", "Kind" },
                unique: true);
        }
    }
}
