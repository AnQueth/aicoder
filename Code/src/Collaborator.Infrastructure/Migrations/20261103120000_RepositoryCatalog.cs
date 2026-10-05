using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Collaborator.Infrastructure.Migrations;

[DbContext(typeof(CollaboratorDb))]
[Migration("20261103120000_RepositoryCatalog")]
public partial class RepositoryCatalog : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "RepositoryCatalogJson",
            table: "Products",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "RepositoryCatalogCommit",
            table: "Products",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "RepositoryCatalogUpdatedAt",
            table: "Products",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "RepositoryCatalogJson",
            table: "Runs",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "RepositoryCommit",
            table: "Runs",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "RepositoryUrl",
            table: "Runs",
            type: "text",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "RepositoryCatalogJson", table: "Products");
        migrationBuilder.DropColumn(name: "RepositoryCatalogCommit", table: "Products");
        migrationBuilder.DropColumn(name: "RepositoryCatalogUpdatedAt", table: "Products");
        migrationBuilder.DropColumn(name: "RepositoryCatalogJson", table: "Runs");
        migrationBuilder.DropColumn(name: "RepositoryCommit", table: "Runs");
        migrationBuilder.DropColumn(name: "RepositoryUrl", table: "Runs");
    }
}
