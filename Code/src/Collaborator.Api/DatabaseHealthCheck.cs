using Collaborator.Infrastructure;
using Microsoft.Extensions.Diagnostics.HealthChecks;

/// <summary>Checks actual database connectivity for Aspire readiness.</summary>
public sealed class DatabaseHealthCheck(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CollaboratorDb>();
        return await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("PostgreSQL is unavailable.");
    }
}
