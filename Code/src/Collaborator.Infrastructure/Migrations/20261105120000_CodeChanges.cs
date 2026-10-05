using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Collaborator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CodeChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CodeChanges",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    PatchJson = table.Column<string>(type: "text", nullable: true),
                    ChangedFilesJson = table.Column<string>(type: "text", nullable: true),
                    BranchName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    BranchCommit = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CodeChanges", x => x.RunId);
                    table.ForeignKey(
                        name: "FK_CodeChanges_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CodeChanges");
        }
    }
}
