using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Collaborator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RepositoryBaseBranch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RepositoryBaseBranch",
                table: "Runs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RepositoryBaseBranch",
                table: "Products",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RepositoryBaseBranch",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "RepositoryBaseBranch",
                table: "Products");
        }
    }
}
