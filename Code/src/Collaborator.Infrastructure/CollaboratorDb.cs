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
        model.Entity<PlanRevisionRecord>().HasKey(x => new { x.RunId, x.Revision });
        model.Entity<PlanRevisionRecord>().Property(x => x.Decision).HasMaxLength(16);
        model.Entity<PlanRevisionRecord>().Property(x => x.Comment).HasMaxLength(InputRules.MaximumCommentLength);
        model.Entity<PlanRevisionRecord>().HasOne<FeatureRun>().WithMany().HasForeignKey(x => x.RunId);
        model.Entity<DeveloperRecord>().HasKey(x => x.UserId);
        model.Entity<DeveloperRecord>().Property(x => x.UserId).HasMaxLength(128);
        model.Entity<DeveloperRecord>().Property(x => x.DisplayName).HasMaxLength(200);
        model.Entity<DeveloperRecord>().Property(x => x.ReviewFocus).HasMaxLength(1_000);
        model.Entity<ReviewAssignmentRecord>().HasKey(x => new { x.RunId, x.Revision, x.DeveloperId });
        model.Entity<ReviewAssignmentRecord>().Property(x => x.DeveloperId).HasMaxLength(128);
        model.Entity<ReviewAssignmentRecord>().Property(x => x.DeveloperDisplayName).HasMaxLength(200);
        model.Entity<ReviewAssignmentRecord>().Property(x => x.ReviewFocus).HasMaxLength(1_000);
        model.Entity<ReviewAssignmentRecord>().Property(x => x.ReviewPrompt).IsRequired();
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
