using Collaborator.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Collaborator.Infrastructure;

/// <summary>PostgreSQL business state, audit events, and transactional outbox.</summary>
public sealed class CollaboratorDb(DbContextOptions<CollaboratorDb> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<FeatureRun> Runs => Set<FeatureRun>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<RunEvent> Events => Set<RunEvent>();
    public DbSet<PlanRevisionRecord> PlanRevisions => Set<PlanRevisionRecord>();
    public DbSet<AgentSkillRecord> AgentSkills => Set<AgentSkillRecord>();
    public DbSet<AgentSkillRevisionRecord> AgentSkillRevisions => Set<AgentSkillRevisionRecord>();
    public DbSet<WorkflowArtifactRecord> WorkflowArtifacts => Set<WorkflowArtifactRecord>();
    public DbSet<StageQuestionRecord> StageQuestions => Set<StageQuestionRecord>();
    public DbSet<StageArtifactReviewRecord> StageArtifactReviews => Set<StageArtifactReviewRecord>();
    public DbSet<FeatureSpecificationRevisionRecord> FeatureSpecificationRevisions => Set<FeatureSpecificationRevisionRecord>();
    public DbSet<DeveloperRecord> Developers => Set<DeveloperRecord>();
    public DbSet<ReviewAssignmentRecord> ReviewAssignments => Set<ReviewAssignmentRecord>();
    public DbSet<CodeChangeRecord> CodeChanges => Set<CodeChangeRecord>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Product>().HasKey(x => x.Id);
        model.Entity<Product>().Property(x => x.Id).HasMaxLength(80);
        model.Entity<Product>().Property(x => x.Name).HasMaxLength(200);
        model.Entity<FeatureRun>().HasIndex(x => new { x.ProductId, x.IdempotencyKey }).IsUnique();
        model.Entity<FeatureRun>().Property(x => x.IdempotencyKey).HasMaxLength(128);
        model.Entity<FeatureRun>().HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId);
        model.Entity<FeatureRun>().Property<uint>("xmin").IsRowVersion();
        model.Entity<FeatureRun>().Property(x => x.Decision).HasMaxLength(16);
        model.Entity<FeatureRun>().Property(x => x.DecisionComment).HasMaxLength(InputRules.MaximumCommentLength);
        model.Entity<FeatureRun>().Property(x => x.BusinessAnalystId).HasMaxLength(128);
        model.Entity<FeatureRun>().Property(x => x.BusinessAnalystSkillId).HasMaxLength(80);
        model.Entity<FeatureRun>().Property(x => x.BusinessAnalystSkillName).HasMaxLength(200);
        model.Entity<FeatureRun>().Property(x => x.BusinessAnalystPrompt)
            .HasMaxLength(InputRules.MaximumBusinessAnalystSkillPromptLength);
        model.Entity<FeatureRun>().Property(x => x.SelectedReviewersJson).HasColumnType("jsonb");
        model.Entity<FeatureRun>().Property(x => x.DeveloperSkillsJson).HasColumnType("jsonb").IsRequired();
        model.Entity<AgentSkillRecord>().HasKey(x => new { x.RoleId, x.Domain, x.SkillId });
        model.Entity<AgentSkillRecord>().Property(x => x.RoleId).HasMaxLength(80);
        model.Entity<AgentSkillRecord>().Property(x => x.Domain).HasMaxLength(80);
        model.Entity<AgentSkillRecord>().Property(x => x.SkillId).HasMaxLength(80);
        model.Entity<AgentSkillRecord>().Property<uint>("xmin").IsRowVersion();
        model.Entity<AgentSkillRevisionRecord>().HasKey(
            x => new { x.RoleId, x.Domain, x.SkillId, x.Revision });
        model.Entity<AgentSkillRevisionRecord>().Property(x => x.RoleId).HasMaxLength(80);
        model.Entity<AgentSkillRevisionRecord>().Property(x => x.Domain).HasMaxLength(80);
        model.Entity<AgentSkillRevisionRecord>().Property(x => x.SkillId).HasMaxLength(80);
        model.Entity<AgentSkillRevisionRecord>().Property(x => x.Name).HasMaxLength(200);
        model.Entity<AgentSkillRevisionRecord>().Property(x => x.Prompt)
            .HasMaxLength(InputRules.MaximumAgentSkillPromptLength);
        model.Entity<AgentSkillRevisionRecord>().Property(x => x.CreatedBy).HasMaxLength(128);
        model.Entity<AgentSkillRevisionRecord>().Property(x => x.ChangeReason)
            .HasMaxLength(InputRules.MaximumCommentLength);
        model.Entity<AgentSkillRevisionRecord>().HasOne<AgentSkillRecord>().WithMany()
            .HasForeignKey(x => new { x.RoleId, x.Domain, x.SkillId });
        model.Entity<WorkflowArtifactRecord>().HasKey(
            x => new { x.RunId, x.Stage, x.ArtifactKey, x.Revision });
        model.Entity<WorkflowArtifactRecord>().Property(x => x.Stage).HasMaxLength(64);
        model.Entity<WorkflowArtifactRecord>().Property(x => x.ArtifactKey).HasMaxLength(120);
        model.Entity<WorkflowArtifactRecord>().Property(x => x.ArtifactType).HasMaxLength(80);
        model.Entity<WorkflowArtifactRecord>().Property(x => x.PayloadJson).HasColumnType("jsonb");
        model.Entity<WorkflowArtifactRecord>().Property(x => x.InputArtifactRevisionsJson)
            .HasColumnType("jsonb").IsRequired();
        model.Entity<WorkflowArtifactRecord>().Property(x => x.SkillRevisionsJson)
            .HasColumnType("jsonb").IsRequired();
        model.Entity<WorkflowArtifactRecord>().Property(x => x.RepositoryCommit).HasMaxLength(64);
        model.Entity<WorkflowArtifactRecord>().Property(x => x.Status).HasMaxLength(32);
        model.Entity<WorkflowArtifactRecord>().Property(x => x.Decision).HasMaxLength(16);
        model.Entity<WorkflowArtifactRecord>().Property(x => x.DecidedBy).HasMaxLength(128);
        model.Entity<WorkflowArtifactRecord>().Property(x => x.DecisionComment)
            .HasMaxLength(InputRules.MaximumCommentLength);
        model.Entity<WorkflowArtifactRecord>().Property(x => x.StaleReason)
            .HasMaxLength(InputRules.MaximumCommentLength);
        model.Entity<WorkflowArtifactRecord>().Property<uint>("xmin").IsRowVersion();
        model.Entity<WorkflowArtifactRecord>().HasOne<FeatureRun>().WithMany()
            .HasForeignKey(x => x.RunId);
        model.Entity<StageQuestionRecord>().HasKey(
            x => new { x.RunId, x.Stage, x.ArtifactKey, x.Revision, x.Index });
        model.Entity<StageQuestionRecord>().Property(x => x.Stage).HasMaxLength(64);
        model.Entity<StageQuestionRecord>().Property(x => x.ArtifactKey).HasMaxLength(120);
        model.Entity<StageQuestionRecord>().Property(x => x.Question).HasMaxLength(InputRules.MaximumCommentLength);
        model.Entity<StageQuestionRecord>().Property(x => x.Answer).HasMaxLength(InputRules.MaximumCommentLength);
        model.Entity<StageQuestionRecord>().Property(x => x.AnsweredBy).HasMaxLength(128);
        model.Entity<StageQuestionRecord>().HasOne<WorkflowArtifactRecord>().WithMany()
            .HasForeignKey(x => new { x.RunId, x.Stage, x.ArtifactKey, x.Revision });
        model.Entity<StageArtifactReviewRecord>().HasKey(
            x => new { x.RunId, x.Stage, x.ArtifactKey, x.Revision, x.ReviewerId });
        model.Entity<StageArtifactReviewRecord>().Property(x => x.Stage).HasMaxLength(64);
        model.Entity<StageArtifactReviewRecord>().Property(x => x.ArtifactKey).HasMaxLength(120);
        model.Entity<StageArtifactReviewRecord>().Property(x => x.ReviewerId).HasMaxLength(128);
        model.Entity<StageArtifactReviewRecord>().Property(x => x.Decision).HasMaxLength(16);
        model.Entity<StageArtifactReviewRecord>().Property(x => x.Comment)
            .HasMaxLength(InputRules.MaximumCommentLength);
        model.Entity<StageArtifactReviewRecord>().HasOne<WorkflowArtifactRecord>().WithMany()
            .HasForeignKey(x => new { x.RunId, x.Stage, x.ArtifactKey, x.Revision });
        model.Entity<FeatureSpecificationRevisionRecord>().HasKey(x => new { x.RunId, x.Revision });
        model.Entity<FeatureSpecificationRevisionRecord>().Property<uint>("xmin").IsRowVersion();
        model.Entity<FeatureSpecificationRevisionRecord>().Property(x => x.Decision).HasMaxLength(16);
        model.Entity<FeatureSpecificationRevisionRecord>().Property(x => x.Comment)
            .HasMaxLength(InputRules.MaximumCommentLength);
        model.Entity<FeatureSpecificationRevisionRecord>().Property(x => x.AnswersJson).HasColumnType("jsonb");
        model.Entity<FeatureSpecificationRevisionRecord>().HasOne<FeatureRun>().WithMany()
            .HasForeignKey(x => x.RunId);
        model.Entity<PlanRevisionRecord>().HasKey(x => new { x.RunId, x.Revision });
        model.Entity<PlanRevisionRecord>().Property(x => x.Decision).HasMaxLength(16);
        model.Entity<PlanRevisionRecord>().Property(x => x.Comment).HasMaxLength(InputRules.MaximumCommentLength);
        model.Entity<PlanRevisionRecord>().HasOne<FeatureRun>().WithMany().HasForeignKey(x => x.RunId);
        model.Entity<DeveloperRecord>().HasKey(x => x.UserId);
        model.Entity<DeveloperRecord>().Property(x => x.UserId).HasMaxLength(128);
        model.Entity<DeveloperRecord>().Property(x => x.DisplayName).HasMaxLength(200);
        model.Entity<DeveloperRecord>().Property(x => x.ReviewFocus).HasMaxLength(1_000);
        model.Entity<DeveloperRecord>().Property(x => x.RolesJson).HasColumnType("jsonb").IsRequired();
        model.Entity<ReviewAssignmentRecord>().HasKey(x => new { x.RunId, x.Revision, x.DeveloperId });
        model.Entity<ReviewAssignmentRecord>().Property(x => x.DeveloperId).HasMaxLength(128);
        model.Entity<ReviewAssignmentRecord>().Property(x => x.DeveloperDisplayName).HasMaxLength(200);
        model.Entity<ReviewAssignmentRecord>().Property(x => x.ReviewFocus).HasMaxLength(1_000);
        model.Entity<ReviewAssignmentRecord>().Property(x => x.MatchedRolesJson).HasColumnType("jsonb").IsRequired();
        model.Entity<ReviewAssignmentRecord>().Property(x => x.ReviewPrompt).IsRequired();
        model.Entity<ReviewAssignmentRecord>().Property(x => x.Decision).HasMaxLength(16);
        model.Entity<ReviewAssignmentRecord>().Property(x => x.Response).HasMaxLength(InputRules.MaximumCommentLength);
        model.Entity<ReviewAssignmentRecord>().Property<uint>("xmin").IsRowVersion();
        model.Entity<ReviewAssignmentRecord>().HasOne<PlanRevisionRecord>().WithMany()
            .HasForeignKey(x => new { x.RunId, x.Revision });
        model.Entity<ReviewAssignmentRecord>().HasOne<DeveloperRecord>().WithMany()
            .HasForeignKey(x => x.DeveloperId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<CodeChangeRecord>().HasKey(x => x.RunId);
        model.Entity<CodeChangeRecord>().Property(x => x.Status).HasMaxLength(32);
        model.Entity<CodeChangeRecord>().Property(x => x.Summary).HasMaxLength(2_000);
        model.Entity<CodeChangeRecord>().Property(x => x.BranchName).HasMaxLength(128);
        model.Entity<CodeChangeRecord>().Property(x => x.BranchCommit).HasMaxLength(64);
        model.Entity<CodeChangeRecord>().Property<uint>("xmin").IsRowVersion();
        model.Entity<CodeChangeRecord>().HasOne<FeatureRun>().WithMany().HasForeignKey(x => x.RunId);
        model.Entity<OutboxMessage>().HasIndex(x => new { x.RunId, x.Kind, x.Revision }).IsUnique();
        model.Entity<OutboxMessage>().HasOne<FeatureRun>().WithMany().HasForeignKey(x => x.RunId);
        model.Entity<RunEvent>().HasIndex(x => new { x.RunId, x.Id });
        model.Entity<RunEvent>().HasOne<FeatureRun>().WithMany().HasForeignKey(x => x.RunId);
    }
}

/// <summary>Registers PostgreSQL persistence; missing connection settings are an error.</summary>
public static class Persistence
{
    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration config)
    {
        var connection = config.GetConnectionString("collaborator")
            ?? throw new InvalidOperationException("ConnectionStrings:collaborator is required.");
        services.AddDbContext<CollaboratorDb>(options => options.UseNpgsql(connection));
        services.AddScoped<RunStore>();
        return services;
    }
}

/// <summary>Supports migration generation without connecting to a real database.</summary>
public sealed class DesignTimeDbFactory : IDesignTimeDbContextFactory<CollaboratorDb>
{
    public CollaboratorDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<CollaboratorDb>()
            .UseNpgsql("Host=localhost;Database=collaborator").Options);
}
