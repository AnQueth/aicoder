using Collaborator.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Net.Http.Json;

/// <summary>Delivers persisted intents with stable run identities and bounded retries.</summary>
public sealed class OutboxDispatcher(
    IServiceScopeFactory scopes, IHttpClientFactory clients, ILogger<OutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or NpgsqlException or DbUpdateException or TimeoutException)
            {
                logger.LogError(ex, "Outbox infrastructure failure; persisted intents remain pending.");
            }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private async Task DispatchAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CollaboratorDb>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Serialize dispatchers across hosts; retries reconcile using the stable durable instance ID.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(714002)", ct);
        var now = DateTimeOffset.UtcNow;
        var message = await db.Outbox.Where(x => x.DeliveredAt == null && x.Attempts < 10 && x.NextAttemptAt <= now)
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (message is null)
        {
            return;
        }
        message.Attempts++;
        try
        {
            using var response = await clients.CreateClient("workflows").PostAsJsonAsync(
                $"api/internal/runs/{message.RunId}/{message.Kind}", new { revision = message.Revision }, ct);
            response.EnsureSuccessStatusCode();
            message.DeliveredAt = DateTimeOffset.UtcNow;
            message.FailureCode = null;
            logger.LogInformation("Delivered {Kind} for run {RunId}.", message.Kind, message.RunId);
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            message.FailureCode = "workflow_dispatch_failed";
            message.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(60, message.Attempts * 5));
            logger.LogError(ex, "Dispatch {Kind} for {RunId} failed on attempt {Attempt}/10.",
                message.Kind, message.RunId, message.Attempts);
            if (message.Attempts == 10)
            {
                var run = await db.Runs.SingleAsync(x => x.Id == message.RunId, ct);
                run.Status = "blocked";
                run.FailureCode = message.FailureCode;
                db.Events.Add(new RunEvent { RunId = run.Id, Kind = "dispatch_exhausted", Actor = "outbox" });
            }
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
