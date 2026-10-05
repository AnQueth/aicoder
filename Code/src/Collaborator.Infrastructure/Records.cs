namespace Collaborator.Infrastructure;

/// <summary>Registered product scope for the initial product-brief pilot.</summary>
public sealed class Product
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Brief { get; set; }
}

/// <summary>Persisted feature intent and durable workflow identity.</summary>
public sealed class FeatureRun
{
    public Guid Id { get; set; }
    public required string ProductId { get; set; }
    public required string Text { get; set; }
    public required string ProductBrief { get; set; }
    public required string IdempotencyKey { get; set; }
    public string Status { get; set; } = "queued";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? PlanJson { get; set; }
    public int PlanRevision { get; set; }
    public bool? Approved { get; set; }
    public string? Decision { get; set; }
    public string? DecisionComment { get; set; }
    public string? DecisionBy { get; set; }
    public string? FailureCode { get; set; }
}

/// <summary>Immutable plan revision with the human decision recorded against it.</summary>
public sealed class PlanRevisionRecord
{
    public Guid RunId { get; set; }
    public int Revision { get; set; }
    public required string PlanJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? Decision { get; set; }
    public string? Comment { get; set; }
    public string? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}

/// <summary>Durable business-to-workflow delivery intent.</summary>
public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public required string Kind { get; set; }
    public int Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DeliveredAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public string? FailureCode { get; set; }
}

/// <summary>Attributable workflow progression without sensitive prompt payloads.</summary>
public sealed class RunEvent
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public required string Kind { get; set; }
    public required string Actor { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
