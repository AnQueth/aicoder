using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Collaborator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PerRunSpecialistSelection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeveloperSkillSelectionJson",
                table: "Runs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeveloperSkillSelectionJson",
                table: "Runs");
        }
    }
}
