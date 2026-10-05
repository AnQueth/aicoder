using Collaborator.Core;
using Collaborator.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults(useHttpResilience: false);
builder.AddKeyVaultSecrets();
builder.Services.AddPersistence(builder.Configuration);
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddHostedService<OutboxDispatcher>();
builder.Services.AddHttpClient("workflows", client =>
{
    client.BaseAddress = new Uri("http://workflows");
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Add("X-Workflow-Key",
        builder.Configuration["Security:WorkflowKey"]
        ?? throw new InvalidOperationException("Security:WorkflowKey is required."));
});
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("postgres");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 32_768);

if (!builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException("This foundation uses development API keys. Production identity is not implemented.");
}
var apiKey = builder.Configuration["Security:ApiKey"];
if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length < 32)
{
    throw new InvalidOperationException("A development Security:ApiKey of at least 32 characters is required.");
}

var app = builder.Build();
app.UseExceptionHandler();
app.MapDefaultEndpoints();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") ||
        context.Request.Path.StartsWithSegments("/openapi"))
    {
        var supplied = context.Request.Headers["X-Api-Key"].ToString();
        if (!CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)),
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied))))
        {
            await Results.Problem(statusCode: 401, title: "Valid development API key required.").ExecuteAsync(context);
            return;
        }
    }
    await next(context);
});
app.MapOpenApi();

var api = app.MapGroup("/api/v1");
api.MapGet("/developers", async (CollaboratorDb db, CancellationToken ct) =>
    Results.Ok(await db.Developers.AsNoTracking().OrderBy(x => x.UserId).ToListAsync(ct)));
api.MapPut("/developers/{userId}", async (
    string userId, DeveloperConfiguration request, CollaboratorDb db, CancellationToken ct) =>
{
    if (!InputRules.IsValidDeveloper(userId, request, out var error))
    {
        return Results.Problem(statusCode: 400, title: error);
    }
    userId = userId.Trim();
    var developer = await db.Developers.SingleOrDefaultAsync(x => x.UserId == userId, ct);
    if (developer is null)
    {
        developer = new DeveloperRecord { UserId = userId, DisplayName = request.DisplayName.Trim() };
        db.Developers.Add(developer);
    }
    developer.DisplayName = request.DisplayName.Trim();
    developer.ReviewFocus = string.IsNullOrWhiteSpace(request.ReviewFocus) ? null : request.ReviewFocus.Trim();
    developer.IsEnabled = request.IsEnabled;
    await db.SaveChangesAsync(ct);
    return Results.Ok(developer);
});
api.MapPost("/products", async (ProductRegistration request, CollaboratorDb db, CancellationToken ct) =>
{
    if (!InputRules.IsProductId(request.Id) || !InputRules.IsText(request.Name, 200) ||
        !InputRules.IsText(request.Brief) ||
        (request.RepositoryUrl is not null && !InputRules.IsLocalFileRepositoryUrl(request.RepositoryUrl)))
    {
        return Results.Problem(statusCode: 400,
            title: "A lowercase product ID, name, and bounded brief are required; repository inspection currently supports local file:// Git URLs only.");
    }
    db.Products.Add(new Product
    {
        Id = request.Id,
        Name = request.Name.Trim(),
        Brief = request.Brief.Trim(),
        RepositoryUrl = request.RepositoryUrl?.Trim()
    });
    try
    {
        await db.SaveChangesAsync(ct);
    }
    catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
    {
        return Results.Problem(statusCode: 409, title: "Product already exists.");
    }
    return Results.Created($"/api/v1/products/{request.Id}", request);
});
api.MapPut("/products/{id}/repository", async (
    string id, RepositorySourceUpdate request, CollaboratorDb db, CancellationToken ct) =>
{
    if (request.RepositoryUrl is not null && !InputRules.IsLocalFileRepositoryUrl(request.RepositoryUrl))
    {
        return Results.Problem(statusCode: 400,
            title: "Repository inspection currently supports local file:// Git URLs only.");
    }
    var product = await db.Products.SingleOrDefaultAsync(x => x.Id == id, ct);
    if (product is null)
    {
        return Results.NotFound();
    }
    product.RepositoryUrl = request.RepositoryUrl?.Trim();
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});
api.MapGet("/products/{id}", async (string id, CollaboratorDb db, CancellationToken ct) =>
    await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) is { } product
        ? Results.Ok(new { product.Id, product.Name, product.Brief, product.RepositoryUrl })
        : Results.NotFound());
api.MapGet("/products/{id}/repository-catalog", async (
    string id, CollaboratorDb db, CancellationToken ct) =>
    await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct) is { } product
        ? Results.Ok(new
        {
            product.Id,
            product.RepositoryUrl,
            commit = product.RepositoryCatalogCommit,
            product.RepositoryCatalogUpdatedAt,
            catalog = product.RepositoryCatalogJson is null
                ? null
                : JsonSerializer.Deserialize<RepositoryCatalog>(product.RepositoryCatalogJson, JsonSerializerOptions.Web)
        })
        : Results.NotFound());

api.MapPost("/products/{id}/feature-requests", async (
    string id, FeatureSubmission request, HttpRequest http, CollaboratorDb db, RunStore store, CancellationToken ct) =>
{
    var key = http.Headers["Idempotency-Key"].ToString();
    if (!InputRules.IsText(request.Text) || !InputRules.IsText(key, 128))
    {
        return Results.Problem(statusCode: 400, title: "Feature text and an Idempotency-Key header are required.");
    }
    var product = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    if (product is null)
    {
        return Results.NotFound();
    }
    var text = request.Text.Trim();
    var existing = await db.Runs.AsNoTracking().SingleOrDefaultAsync(
        x => x.ProductId == id && x.IdempotencyKey == key, ct);
    if (existing is not null)
    {
        return SubmissionResult(existing, text);
    }
    if (!InputRules.IsLocalFileRepositoryUrl(product.RepositoryUrl))
    {
        return Results.Problem(statusCode: 409,
            title: "Configure a local file:// Git repository before submitting a feature request.");
    }
    var run = new FeatureRun
    {
        Id = Guid.NewGuid(), ProductId = id, Text = text,
        ProductBrief = product.Brief, IdempotencyKey = key, RepositoryUrl = product.RepositoryUrl
    };
    db.Runs.Add(run);
    db.Outbox.Add(new OutboxMessage { Id = Guid.NewGuid(), RunId = run.Id, Kind = "start" });
    store.AddEvent(run.Id, "submitted", "development-api-key");
    try
    {
        await db.SaveChangesAsync(ct);
    }
    catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
    {
        db.ChangeTracker.Clear();
        var winner = await db.Runs.AsNoTracking().SingleAsync(
            x => x.ProductId == id && x.IdempotencyKey == key, ct);
        return SubmissionResult(winner, text);
    }
    return SubmissionResult(run, text);
});
api.MapGet("/runs/{id:guid}", async (Guid id, CollaboratorDb db, CancellationToken ct) =>
{
    var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
    if (run is null)
    {
        return Results.NotFound();
    }
    var codeChange = await db.CodeChanges.AsNoTracking()
        .SingleOrDefaultAsync(x => x.RunId == id, ct);
    return Results.Ok(new
    {
        run.Id, run.ProductId, run.Text, run.Status, run.CreatedAt,
        run.PlanRevision, run.Approved, run.Decision, run.DecisionComment, run.FailureCode,
        run.RepositoryCommit,
        repositoryCatalog = run.RepositoryCatalogJson is null
            ? null
            : JsonSerializer.Deserialize<RepositoryCatalog>(run.RepositoryCatalogJson, JsonSerializerOptions.Web),
        plan = run.PlanJson is null ? null : JsonSerializer.Deserialize<DraftPlan>(run.PlanJson, JsonSerializerOptions.Web),
        codeChange = codeChange is null ? null : new CodeChangeStatus(
            codeChange.Status,
            codeChange.Summary,
            codeChange.BranchName,
            codeChange.BranchCommit,
            codeChange.ChangedFilesJson is null
                ? []
                : JsonSerializer.Deserialize<string[]>(codeChange.ChangedFilesJson, JsonSerializerOptions.Web) ?? [],
            run.FailureCode)
    });
});
api.MapGet("/runs/{id:guid}/events", async (
    Guid id, long? after, CollaboratorDb db, CancellationToken ct) =>
{
    if (!await db.Runs.AnyAsync(x => x.Id == id, ct))
    {
        return Results.NotFound();
    }
    return Results.Ok(await db.Events.AsNoTracking()
        .Where(x => x.RunId == id && x.Id > (after ?? 0))
        .OrderBy(x => x.Id).Take(100).ToListAsync(ct));
});
api.MapGet("/runs/{id:guid}/plans", async (Guid id, CollaboratorDb db, CancellationToken ct) =>
{
    if (!await db.Runs.AnyAsync(x => x.Id == id, ct))
    {
        return Results.NotFound();
    }
    var revisions = await db.PlanRevisions.AsNoTracking()
        .Where(x => x.RunId == id).OrderBy(x => x.Revision).ToListAsync(ct);
    return Results.Ok(revisions.Select(x => new
    {
        x.Revision, x.CreatedAt, x.Decision, x.Comment, x.DecidedBy, x.DecidedAt,
        plan = JsonSerializer.Deserialize<DraftPlan>(x.PlanJson, JsonSerializerOptions.Web)
    }));
});
api.MapGet("/runs/{id:guid}/assignments", async (Guid id, CollaboratorDb db, CancellationToken ct) =>
{
    if (!await db.Runs.AnyAsync(x => x.Id == id, ct))
    {
        return Results.NotFound();
    }
    return Results.Ok(await (
        from assignment in db.ReviewAssignments.AsNoTracking()
        where assignment.RunId == id
        orderby assignment.Revision, assignment.DeveloperId
        select new
        {
            assignment.RunId,
            assignment.Revision,
            UserId = assignment.DeveloperId,
            assignment.DeveloperDisplayName,
            assignment.ReviewFocus,
            assignment.ReviewPrompt,
            assignment.Response,
            status = assignment.RespondedAt == null ? "pending" : "completed",
            assignment.AssignedAt,
            assignment.RespondedAt
        }).ToListAsync(ct));
});
api.MapPost("/runs/{id:guid}/assignments/{userId}/response", async (
    Guid id, string userId, ReviewAssignmentResponse request, CollaboratorDb db, RunStore store, CancellationToken ct) =>
{
    if (!InputRules.IsValidReviewAssignmentResponse(request, out var error))
    {
        return Results.Problem(statusCode: 400, title: error);
    }
    userId = userId.Trim();
    var run = await db.Runs.SingleOrDefaultAsync(x => x.Id == id, ct);
    if (run is null)
    {
        return Results.NotFound();
    }
    var assignment = await db.ReviewAssignments.SingleOrDefaultAsync(
        x => x.RunId == id && x.Revision == run.PlanRevision && x.DeveloperId == userId, ct);
    if (assignment is null)
    {
        return Results.NotFound();
    }
    var response = request.Response.Trim();
    if (assignment.RespondedAt is not null)
    {
        return assignment.Response == response
            ? Results.Accepted($"/api/v1/runs/{id}/assignments")
            : Results.Problem(statusCode: 409, title: "This developer has already responded to the assignment.");
    }
    if (run.Status != "awaiting_approval")
    {
        return Results.Problem(statusCode: 409, title: "The run is not awaiting review for this plan revision.");
    }
    assignment.Response = response;
    assignment.RespondedAt = DateTimeOffset.UtcNow;
    store.AddEvent(id, "developer_review_submitted", userId);
    try
    {
        await db.SaveChangesAsync(ct);
    }
    catch (DbUpdateConcurrencyException)
    {
        return Results.Problem(statusCode: 409, title: "A response was already recorded for this assignment.");
    }
    return Results.Accepted($"/api/v1/runs/{id}/assignments");
});
api.MapPost("/runs/{id:guid}/plan-decisions", async (
    Guid id, PlanDecision decision, CollaboratorDb db, RunStore store, CancellationToken ct) =>
{
    if (!InputRules.IsValidDecision(decision, out var error))
    {
        return Results.Problem(statusCode: 400, title: error);
    }
    var run = await db.Runs.SingleOrDefaultAsync(x => x.Id == id, ct);
    if (run is null)
    {
        return Results.NotFound();
    }
    var record = await db.PlanRevisions.SingleOrDefaultAsync(
        x => x.RunId == id && x.Revision == decision.Revision, ct);
    if (record is null)
    {
        return Results.Problem(statusCode: 409, title: "Decision requires a generated plan revision.");
    }
    if (record.Decision is not null)
    {
        return record.Decision == decision.Decision
            ? Results.Accepted($"/api/v1/runs/{id}")
            : Results.Problem(statusCode: 409, title: "This plan revision already has a different decision.");
    }
    if (decision.Revision != run.PlanRevision || run.Status != "awaiting_approval")
    {
        return Results.Problem(statusCode: 409, title: "The run is not awaiting a decision on this plan revision.");
    }
    if (decision.Decision == PlanDecisionKinds.Revise && run.PlanRevision >= InputRules.MaximumPlanRevisions)
    {
        return Results.Problem(statusCode: 409,
            title: $"The revision limit of {InputRules.MaximumPlanRevisions} has been reached; approve or reject this plan.");
    }

    const string actor = "development-api-key";
    var comment = string.IsNullOrWhiteSpace(decision.Comment) ? null : decision.Comment.Trim();
    run.Decision = decision.Decision;
    run.DecisionComment = comment;
    run.DecisionBy = actor;
    run.Approved = decision.Decision switch
    {
        PlanDecisionKinds.Approve => true,
        PlanDecisionKinds.Reject => false,
        _ => null
    };
    record.Decision = decision.Decision;
    record.Comment = comment;
    record.DecidedBy = actor;
    record.DecidedAt = DateTimeOffset.UtcNow;
    if (decision.Decision == PlanDecisionKinds.Revise)
    {
        run.Status = "revision_requested";
        store.AddEvent(id, "revision_requested", actor);
    }
    else
    {
        store.AddEvent(id, "decision_recorded", actor);
    }
    db.Outbox.Add(new OutboxMessage { Id = Guid.NewGuid(), RunId = id, Kind = "decision", Revision = decision.Revision });
    try
    {
        await db.SaveChangesAsync(ct);
    }
    catch (DbUpdateConcurrencyException)
    {
        return Results.Problem(statusCode: 409, title: "Run changed concurrently; reload before deciding.");
    }
    catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
    {
        return Results.Problem(statusCode: 409, title: "A decision for this plan revision was recorded concurrently.");
    }
    return Results.Accepted($"/api/v1/runs/{id}");
});
app.Run();

static IResult SubmissionResult(FeatureRun run, string text) =>
    run.Text != text
        ? Results.Problem(statusCode: 409, title: "Idempotency key was already used with different feature text.")
        : Results.Accepted($"/api/v1/runs/{run.Id}", new
        {
            featureRequestId = run.Id, runId = run.Id, run.Status,
            statusUrl = $"/api/v1/runs/{run.Id}"
        });
