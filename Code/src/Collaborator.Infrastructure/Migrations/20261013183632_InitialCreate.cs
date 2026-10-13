using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Collaborator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentSkills",
                columns: table => new
                {
                    RoleId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    SkillId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Domain = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ActiveRevision = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentSkills", x => new { x.RoleId, x.Domain, x.SkillId });
                });

            migrationBuilder.CreateTable(
                name: "Developers",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ReviewFocus = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RolesJson = table.Column<string>(type: "jsonb", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Developers", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "Products",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Brief = table.Column<string>(type: "text", nullable: false),
                    RepositoryUrl = table.Column<string>(type: "text", nullable: true),
                    RepositoryCatalogCommit = table.Column<string>(type: "text", nullable: true),
                    RepositoryCatalogJson = table.Column<string>(type: "text", nullable: true),
                    RepositoryCatalogUpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentSkillRevisions",
                columns: table => new
                {
                    RoleId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    SkillId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Domain = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Prompt = table.Column<string>(type: "character varying(12000)", maxLength: 12000, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ChangeReason = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentSkillRevisions", x => new { x.RoleId, x.Domain, x.SkillId, x.Revision });
                    table.ForeignKey(
                        name: "FK_AgentSkillRevisions_AgentSkills_RoleId_Domain_SkillId",
                        columns: x => new { x.RoleId, x.Domain, x.SkillId },
                        principalTable: "AgentSkills",
                        principalColumns: new[] { "RoleId", "Domain", "SkillId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<string>(type: "character varying(80)", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    ProductBrief = table.Column<string>(type: "text", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RepositoryUrl = table.Column<string>(type: "text", nullable: true),
                    RepositoryCommit = table.Column<string>(type: "text", nullable: true),
                    RepositoryCatalogJson = table.Column<string>(type: "text", nullable: true),
                    BusinessAnalystId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    BusinessAnalystSkillId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    BusinessAnalystSkillName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BusinessAnalystSkillRevision = table.Column<int>(type: "integer", nullable: false),
                    BusinessAnalystPrompt = table.Column<string>(type: "character varying(12000)", maxLength: 12000, nullable: false),
                    FeatureSpecificationRevision = table.Column<int>(type: "integer", nullable: false),
                    BusinessAnalystApproved = table.Column<bool>(type: "boolean", nullable: false),
                    ReviewerCount = table.Column<int>(type: "integer", nullable: false),
                    SelectedReviewersJson = table.Column<string>(type: "jsonb", nullable: true),
                    DeveloperSkillsJson = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PlanJson = table.Column<string>(type: "text", nullable: true),
                    PlanRevision = table.Column<int>(type: "integer", nullable: false),
                    Decision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    DecisionComment = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    DecisionBy = table.Column<string>(type: "text", nullable: true),
                    FailureCode = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Runs_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

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

            migrationBuilder.CreateTable(
                name: "Events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Actor = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Events_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FeatureSpecificationRevisions",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    SpecificationJson = table.Column<string>(type: "text", nullable: false),
                    AnswersJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Decision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Comment = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    DecidedBy = table.Column<string>(type: "text", nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatureSpecificationRevisions", x => new { x.RunId, x.Revision });
                    table.ForeignKey(
                        name: "FK_FeatureSpecificationRevisions_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Outbox",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FailureCode = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Outbox", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Outbox_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

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

            migrationBuilder.CreateTable(
                name: "WorkflowArtifacts",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Stage = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ArtifactKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    ArtifactType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    InputArtifactRevisionsJson = table.Column<string>(type: "jsonb", nullable: false),
                    SkillRevisionsJson = table.Column<string>(type: "jsonb", nullable: false),
                    RepositoryCommit = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Decision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    DecidedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    DecisionComment = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    StaleAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StaleReason = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowArtifacts", x => new { x.RunId, x.Stage, x.ArtifactKey, x.Revision });
                    table.ForeignKey(
                        name: "FK_WorkflowArtifacts_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReviewAssignments",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    DeveloperId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DeveloperDisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ReviewFocus = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    MatchedRolesJson = table.Column<string>(type: "jsonb", nullable: false),
                    ReviewPrompt = table.Column<string>(type: "text", nullable: false),
                    Decision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Response = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    AssignedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RespondedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReviewAssignments", x => new { x.RunId, x.Revision, x.DeveloperId });
                    table.ForeignKey(
                        name: "FK_ReviewAssignments_Developers_DeveloperId",
                        column: x => x.DeveloperId,
                        principalTable: "Developers",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReviewAssignments_PlanRevisions_RunId_Revision",
                        columns: x => new { x.RunId, x.Revision },
                        principalTable: "PlanRevisions",
                        principalColumns: new[] { "RunId", "Revision" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StageArtifactReviews",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Stage = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ArtifactKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    ReviewerId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Decision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Comment = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StageArtifactReviews", x => new { x.RunId, x.Stage, x.ArtifactKey, x.Revision, x.ReviewerId });
                    table.ForeignKey(
                        name: "FK_StageArtifactReviews_WorkflowArtifacts_RunId_Stage_Artifact~",
                        columns: x => new { x.RunId, x.Stage, x.ArtifactKey, x.Revision },
                        principalTable: "WorkflowArtifacts",
                        principalColumns: new[] { "RunId", "Stage", "ArtifactKey", "Revision" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StageQuestions",
                columns: table => new
                {
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Stage = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ArtifactKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    Index = table.Column<int>(type: "integer", nullable: false),
                    Question = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Answer = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    AnsweredBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AnsweredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StageQuestions", x => new { x.RunId, x.Stage, x.ArtifactKey, x.Revision, x.Index });
                    table.ForeignKey(
                        name: "FK_StageQuestions_WorkflowArtifacts_RunId_Stage_ArtifactKey_Re~",
                        columns: x => new { x.RunId, x.Stage, x.ArtifactKey, x.Revision },
                        principalTable: "WorkflowArtifacts",
                        principalColumns: new[] { "RunId", "Stage", "ArtifactKey", "Revision" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Events_RunId_Id",
                table: "Events",
                columns: new[] { "RunId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Outbox_RunId_Kind_Revision",
                table: "Outbox",
                columns: new[] { "RunId", "Kind", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReviewAssignments_DeveloperId",
                table: "ReviewAssignments",
                column: "DeveloperId");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_ProductId_IdempotencyKey",
                table: "Runs",
                columns: new[] { "ProductId", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentSkillRevisions");

            migrationBuilder.DropTable(
                name: "CodeChanges");

            migrationBuilder.DropTable(
                name: "Events");

            migrationBuilder.DropTable(
                name: "FeatureSpecificationRevisions");

            migrationBuilder.DropTable(
                name: "Outbox");

            migrationBuilder.DropTable(
                name: "ReviewAssignments");

            migrationBuilder.DropTable(
                name: "StageArtifactReviews");

            migrationBuilder.DropTable(
                name: "StageQuestions");

            migrationBuilder.DropTable(
                name: "AgentSkills");

            migrationBuilder.DropTable(
                name: "Developers");

            migrationBuilder.DropTable(
                name: "PlanRevisions");

            migrationBuilder.DropTable(
                name: "WorkflowArtifacts");

            migrationBuilder.DropTable(
                name: "Runs");

            migrationBuilder.DropTable(
                name: "Products");
        }
    }
}
