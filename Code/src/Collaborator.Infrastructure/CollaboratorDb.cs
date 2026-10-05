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
