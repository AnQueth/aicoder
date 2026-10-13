using Collaborator.Core;
using Collaborator.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http.HttpResults;
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
    developer.RolesJson = JsonSerializer.Serialize(
        request.Roles!.Select(role => role.Trim()).ToArray(), JsonSerializerOptions.Web);
    developer.IsEnabled = request.IsEnabled;
    await db.SaveChangesAsync(ct);
    return Results.Ok(developer);
});
api.MapGet("/agent-skills", async (
    string? roleId, string? domain, string? roleIdPrefix, CollaboratorDb db, CancellationToken ct) =>
{
    if (!string.IsNullOrWhiteSpace(roleIdPrefix) && roleIdPrefix != "developer-")
    {
        return Results.Problem(statusCode: 400,
            title: "roleIdPrefix currently supports the developer- prefix only.");
    }
    var query =
        from skill in db.AgentSkills.AsNoTracking()
        join revision in db.AgentSkillRevisions.AsNoTracking()
            on new { skill.RoleId, skill.Domain, skill.SkillId, Revision = skill.ActiveRevision }
            equals new { revision.RoleId, revision.Domain, revision.SkillId, revision.Revision }
        select new
        {
            skill.RoleId,
            skill.Domain,
            skill.SkillId,
            revision.Name,
            revision.Prompt,
            skill.ActiveRevision,
            skill.IsEnabled,
            skill.UpdatedAt,
            revision.CreatedAt,
            revision.CreatedBy,
            revision.ChangeReason
        };
    if (!string.IsNullOrWhiteSpace(roleId))
    {
        query = query.Where(skill => skill.RoleId == roleId);
    }
    if (!string.IsNullOrWhiteSpace(domain))
    {
        query = query.Where(skill => skill.Domain == domain);
    }
    if (!string.IsNullOrWhiteSpace(roleIdPrefix))
    {
        query = query.Where(skill => skill.RoleId.StartsWith(roleIdPrefix));
    }
    return Results.Ok(await query.OrderBy(skill => skill.RoleId)
        .ThenBy(skill => skill.Domain).ThenBy(skill => skill.SkillId).ToListAsync(ct));
});
api.MapGet("/agent-skills/{roleId}/{domain}/{skillId}/revisions", async (
    string roleId, string domain, string skillId, CollaboratorDb db, CancellationToken ct) =>
{
    if (!InputRules.IsProductId(roleId) || !InputRules.IsProductId(domain) ||
        !InputRules.IsProductId(skillId))
    {
        return Results.Problem(statusCode: 400, title: "Role, domain, and skill IDs must be lowercase identifiers.");
    }
    var revisions = await db.AgentSkillRevisions.AsNoTracking()
        .Where(revision => revision.RoleId == roleId && revision.Domain == domain &&
            revision.SkillId == skillId)
        .OrderByDescending(revision => revision.Revision)
        .ToListAsync(ct);
    return revisions.Count == 0
        ? Results.NotFound()
        : Results.Ok(revisions);
});
api.MapPost("/agent-skills/{roleId}/{domain}/{skillId}/revisions", (
    string roleId, string domain, string skillId, AgentSkillRevisionRequest request,
    CollaboratorDb db, CancellationToken ct) =>
    CreateAgentSkillRevisionAsync(roleId, domain, skillId, request, db, ct));

api.MapGet("/business-analysts/{analystId}/skills", async (
    string analystId, CollaboratorDb db, CancellationToken ct) =>
{
    if (!InputRules.IsText(analystId, 128))
    {
        return Results.Problem(statusCode: 400, title: "A valid business analyst ID is required.");
    }
    var skills =
        from skill in db.AgentSkills.AsNoTracking()
        join revision in db.AgentSkillRevisions.AsNoTracking()
            on new { skill.RoleId, skill.Domain, skill.SkillId, Revision = skill.ActiveRevision }
            equals new { revision.RoleId, revision.Domain, revision.SkillId, revision.Revision }
        where skill.RoleId == "business-analyst" && skill.Domain == "general"
        orderby skill.SkillId
        select new
        {
            AnalystId = analystId,
            skill.SkillId,
            revision.Name,
            revision.Prompt,
            Revision = skill.ActiveRevision,
            skill.IsEnabled,
            skill.UpdatedAt
        };
    return Results.Ok(await skills.ToListAsync(ct));
});
api.MapPut("/business-analysts/{analystId}/skills/{skillId}", async (
    string analystId, string skillId, BusinessAnalystSkillConfiguration request,
    CollaboratorDb db, CancellationToken ct) =>
{
    if (!InputRules.IsValidBusinessAnalystSkill(analystId, skillId, request, out var error))
    {
        return Results.Problem(statusCode: 400, title: error);
    }
    return await CreateAgentSkillRevisionAsync(
        "business-analyst", "general", skillId,
        new AgentSkillRevisionRequest(
            request.Name, request.Prompt, request.IsEnabled,
            "Updated via the legacy business analyst skill endpoint", analystId),
        db, ct);
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
    if (!InputRules.IsValidFeatureSubmission(request, out var submissionError) ||
        !InputRules.IsText(key, 128))
    {
        return Results.Problem(statusCode: 400,
            title: submissionError.Length > 0 ? submissionError : "An Idempotency-Key header is required.");
    }
    var analystId = request.BusinessAnalystId.Trim();
    var skillId = request.BusinessAnalystSkillId.Trim();
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
        return SubmissionResult(existing, text, analystId, skillId, request.ReviewerCount);
    }
    var skill = await db.AgentSkills.AsNoTracking().SingleOrDefaultAsync(
        x => x.RoleId == "business-analyst" && x.Domain == "general" &&
            x.SkillId == skillId && x.IsEnabled, ct);
    var skillRevision = skill is null
        ? null
        : await db.AgentSkillRevisions.AsNoTracking().SingleOrDefaultAsync(
            x => x.RoleId == skill.RoleId && x.Domain == skill.Domain &&
                x.SkillId == skill.SkillId && x.Revision == skill.ActiveRevision, ct);
    if (skill is null || skillRevision is null)
    {
        return Results.Problem(statusCode: 409,
            title: "Configure an enabled business analyst skill before submitting a feature request.");
    }
    var developerSkills =
        await (from developerSkill in db.AgentSkills.AsNoTracking()
               join revision in db.AgentSkillRevisions.AsNoTracking()
                   on new
                   {
                       developerSkill.RoleId,
                       developerSkill.Domain,
                       developerSkill.SkillId,
                       Revision = developerSkill.ActiveRevision
                   }
                   equals new
                   {
                       revision.RoleId,
                       revision.Domain,
                       revision.SkillId,
                       revision.Revision
                   }
               where developerSkill.IsEnabled &&
                   developerSkill.RoleId.StartsWith("developer-")
               orderby developerSkill.RoleId, developerSkill.Domain, developerSkill.SkillId
               select new DeveloperSkillSnapshot(
                   new AgentSkillRevisionReference(
                       developerSkill.RoleId, developerSkill.SkillId,
                       developerSkill.Domain, developerSkill.ActiveRevision),
                   revision.Name,
                   revision.Prompt))
            .ToArrayAsync(ct);
    if (developerSkills.Length == 0)
    {
        return Results.Problem(statusCode: 409,
            title: "Configure at least one enabled developer-* skill before submitting a feature request.");
    }
    if (!InputRules.IsLocalFileRepositoryUrl(product.RepositoryUrl))
    {
        return Results.Problem(statusCode: 409,
            title: "Configure a local file:// Git repository before submitting a feature request.");
    }
    var run = new FeatureRun
    {
        Id = Guid.NewGuid(), ProductId = id, Text = text,
        ProductBrief = product.Brief, IdempotencyKey = key, RepositoryUrl = product.RepositoryUrl,
        BusinessAnalystId = analystId,
        BusinessAnalystSkillId = skill.SkillId,
        BusinessAnalystSkillName = skillRevision.Name,
        BusinessAnalystSkillRevision = skillRevision.Revision,
        BusinessAnalystPrompt = skillRevision.Prompt,
        DeveloperSkillsJson = JsonSerializer.Serialize(developerSkills, JsonSerializerOptions.Web),
        ReviewerCount = request.ReviewerCount,
        BusinessAnalystApproved = false
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
        return SubmissionResult(winner, text, analystId, skillId, request.ReviewerCount);
    }
    return SubmissionResult(run, text, analystId, skillId, request.ReviewerCount);
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
    var architectureRevision = await db.WorkflowArtifacts.AsNoTracking()
        .Where(x => x.RunId == id && x.Stage == WorkflowStages.Architecture &&
            x.ArtifactKey == "architecture-analysis")
        .MaxAsync(x => (int?)x.Revision, ct) ?? 0;
    var specificationJson = run.FeatureSpecificationRevision == 0
        ? null
        : await db.FeatureSpecificationRevisions.AsNoTracking()
            .Where(x => x.RunId == id && x.Revision == run.FeatureSpecificationRevision)
            .Select(x => x.SpecificationJson).SingleOrDefaultAsync(ct);
    return Results.Ok(new
    {
        run.Id, run.ProductId, run.Text, run.Status, run.CreatedAt,
        run.BusinessAnalystId, run.BusinessAnalystSkillId, run.BusinessAnalystSkillRevision,
        run.ReviewerCount,
        run.FeatureSpecificationRevision, run.BusinessAnalystApproved,
        architectureRevision,
        developerSkillCount = JsonSerializer.Deserialize<DeveloperSkillSnapshot[]>(
            run.DeveloperSkillsJson, JsonSerializerOptions.Web)?.Length ?? 0,
        run.PlanRevision, run.Decision, run.DecisionComment, run.FailureCode,
        run.RepositoryCommit,
        repositoryCatalog = run.RepositoryCatalogJson is null
            ? null
            : JsonSerializer.Deserialize<RepositoryCatalog>(run.RepositoryCatalogJson, JsonSerializerOptions.Web),
        plan = run.PlanJson is null ? null : JsonSerializer.Deserialize<DraftPlan>(run.PlanJson, JsonSerializerOptions.Web),
        featureSpecification = specificationJson is null
            ? null
            : JsonSerializer.Deserialize<FeatureSpecification>(specificationJson, JsonSerializerOptions.Web),
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
    api.MapGet("/runs/{id:guid}/feature-specifications", async (
        Guid id, CollaboratorDb db, CancellationToken ct) =>
    {
        if (!await db.Runs.AnyAsync(x => x.Id == id, ct))
        {
            return Results.NotFound();
        }
        var revisions = await db.FeatureSpecificationRevisions.AsNoTracking()
            .Where(x => x.RunId == id).OrderBy(x => x.Revision).ToListAsync(ct);
        return Results.Ok(revisions.Select(x => new
        {
            x.Revision, x.CreatedAt, x.Decision, x.Comment, x.DecidedBy, x.DecidedAt,
            specification = JsonSerializer.Deserialize<FeatureSpecification>(
                x.SpecificationJson, JsonSerializerOptions.Web),
            answers = x.AnswersJson is null
                ? []
                : JsonSerializer.Deserialize<BusinessAnalystQuestionAnswer[]>(
                    x.AnswersJson, JsonSerializerOptions.Web) ?? []
        }));
    });
    api.MapPut("/runs/{id:guid}/feature-specifications/{revision:int}", async (
        Guid id, int revision, FeatureSpecification specification,
        CollaboratorDb db, RunStore store, CancellationToken ct) =>
    {
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (run is null)
        {
            return Results.NotFound();
        }
        if (revision < 1)
        {
            return Results.Problem(statusCode: 400, title: "A positive feature-specification revision is required.");
        }
        try
        {
            await store.UpdateFeatureSpecificationAsync(id, revision, specification, run.BusinessAnalystId, ct);
        }
        catch (InvalidDataException ex)
        {
            return Results.Problem(statusCode: 400, title: ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(statusCode: 409, title: ex.Message);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Problem(statusCode: 409, title: "The feature specification changed concurrently; reload before editing.");
        }
        return Results.NoContent();
    });
    api.MapPost("/runs/{id:guid}/business-analyst-decisions", async (
        Guid id, BusinessAnalystDecision decision, CollaboratorDb db, RunStore store, CancellationToken ct) =>
    {
        if (!InputRules.IsValidBusinessAnalystDecision(decision, out var error))
        {
            return Results.Problem(statusCode: 400, title: error);
        }
        var run = await db.Runs.SingleOrDefaultAsync(x => x.Id == id, ct);
        if (run is null)
        {
            return Results.NotFound();
        }
        if (decision.ActorId is not null &&
            !string.Equals(decision.ActorId.Trim(), run.BusinessAnalystId, StringComparison.Ordinal))
        {
            return Results.Problem(statusCode: 403, title: "The supplied analyst actor does not own this feature review.");
        }
        var revision = await db.FeatureSpecificationRevisions.SingleOrDefaultAsync(
            x => x.RunId == id && x.Revision == decision.Revision, ct);
        if (revision is null)
        {
            return Results.Problem(statusCode: 409, title: "Decision requires a generated feature specification.");
        }
        if (revision.Decision is not null)
        {
            return revision.Decision == decision.Decision
                ? Results.Accepted($"/api/v1/runs/{id}")
                : Results.Problem(statusCode: 409, title: "This feature specification already has a different decision.");
        }
        if (decision.Revision != run.FeatureSpecificationRevision ||
            run.Status != "awaiting_business_analyst_review")
        {
            return Results.Problem(statusCode: 409,
                title: "The run is not awaiting a decision on this feature-specification revision.");
        }
        if (decision.Decision == BusinessAnalystDecisionKinds.Revise &&
            decision.Revision >= InputRules.MaximumPlanRevisions)
        {
            return Results.Problem(statusCode: 409,
                title: $"The revision limit of {InputRules.MaximumPlanRevisions} has been reached; approve or reject this specification.");
        }
        try
        {
            await store.RecordBusinessAnalystDecisionAsync(
                id, decision, decision.ActorId?.Trim() ?? run.BusinessAnalystId, ct);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(statusCode: 409, title: ex.Message);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Problem(statusCode: 409, title: "The run changed concurrently; reload before deciding.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Results.Problem(statusCode: 409, title: "A decision for this feature specification was recorded concurrently.");
        }
        return Results.Accepted($"/api/v1/runs/{id}");
    });
    api.MapGet("/runs/{id:guid}/architecture", async (
        Guid id, CollaboratorDb db, CancellationToken ct) =>
    {
        if (!await db.Runs.AnyAsync(x => x.Id == id, ct))
        {
            return Results.NotFound();
        }
        var artifacts = await db.WorkflowArtifacts.AsNoTracking()
            .Where(x => x.RunId == id && x.Stage == WorkflowStages.Architecture &&
                x.ArtifactKey == "architecture-analysis")
            .OrderBy(x => x.Revision)
            .ToListAsync(ct);
        return Results.Ok(artifacts.Select(artifact => new
        {
            artifact.Revision,
            artifact.Status,
            analysis = JsonSerializer.Deserialize<ArchitectureAnalysis>(
                artifact.PayloadJson, JsonSerializerOptions.Web),
            artifact.InputArtifactRevisionsJson,
            skillRevisions = JsonSerializer.Deserialize<AgentSkillRevisionReference[]>(
                artifact.SkillRevisionsJson, JsonSerializerOptions.Web) ?? [],
            artifact.RepositoryCommit,
            artifact.CreatedAt,
            artifact.Decision,
            artifact.DecidedBy,
            artifact.DecidedAt,
            artifact.DecisionComment,
            artifact.StaleAt,
            artifact.StaleReason
        }));
    });
    api.MapPost("/runs/{id:guid}/architecture-decisions", async (
        Guid id, ArchitectureAnalysisDecision decision, CollaboratorDb db,
        RunStore store, CancellationToken ct) =>
    {
        if (!InputRules.IsValidArchitectureAnalysisDecision(decision, out var error))
        {
            return Results.Problem(statusCode: 400, title: error);
        }
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (run is null)
        {
            return Results.NotFound();
        }
        var artifact = await db.WorkflowArtifacts.AsNoTracking().SingleOrDefaultAsync(
            x => x.RunId == id && x.Stage == WorkflowStages.Architecture &&
                x.ArtifactKey == "architecture-analysis" && x.Revision == decision.Revision,
            ct);
        if (artifact is null)
        {
            return Results.Problem(statusCode: 409, title: "Decision requires a generated architecture analysis.");
        }
        if (artifact.Decision is not null)
        {
            var comment = string.IsNullOrWhiteSpace(decision.Comment) ? null : decision.Comment.Trim();
            return artifact.Decision == decision.Decision &&
                artifact.DecisionComment == comment &&
                artifact.DecidedBy == (decision.ActorId?.Trim() ?? "development-api-key")
                    ? Results.Accepted($"/api/v1/runs/{id}/architecture")
                    : Results.Problem(statusCode: 409,
                        title: "This architecture revision already has a different decision.");
        }
        if (decision.Revision != await db.WorkflowArtifacts.AsNoTracking()
                .Where(x => x.RunId == id && x.Stage == WorkflowStages.Architecture &&
                    x.ArtifactKey == "architecture-analysis")
                .MaxAsync(x => (int?)x.Revision, ct) ||
            run.Status != "awaiting_architecture_review")
        {
            return Results.Problem(statusCode: 409,
                title: "The run is not awaiting a decision on this architecture revision.");
        }
        try
        {
            await store.RecordArchitectureAnalysisDecisionAsync(
                id, decision, decision.ActorId?.Trim() ?? "development-api-key", ct);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(statusCode: 409, title: ex.Message);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Problem(statusCode: 409, title: "The architecture analysis changed concurrently; reload before deciding.");
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Results.Problem(statusCode: 409, title: "A decision for this architecture revision was recorded concurrently.");
        }
        return Results.Accepted($"/api/v1/runs/{id}/architecture");
    });
    api.MapGet("/runs/{id:guid}/specialist-analyses", async (
        Guid id, CollaboratorDb db, CancellationToken ct) =>
    {
        if (!await db.Runs.AnyAsync(x => x.Id == id, ct))
        {
            return Results.NotFound();
        }
        var artifact = await db.WorkflowArtifacts.AsNoTracking().SingleOrDefaultAsync(
            x => x.RunId == id && x.Stage == WorkflowStages.SpecialistAnalysis &&
                x.ArtifactKey == "developer-skills" && x.Revision == 1,
            ct);
        return artifact is null
            ? Results.Ok(Array.Empty<object>())
            : Results.Ok(new[]
            {
                new
                {
                    artifact.Revision,
                    artifact.Status,
                    analyses = JsonSerializer.Deserialize<DeveloperSkillAnalysis[]>(
                        artifact.PayloadJson, JsonSerializerOptions.Web),
                    artifact.InputArtifactRevisionsJson,
                    skillRevisions = JsonSerializer.Deserialize<AgentSkillRevisionReference[]>(
                        artifact.SkillRevisionsJson, JsonSerializerOptions.Web) ?? [],
                    artifact.RepositoryCommit,
                    artifact.CreatedAt
                }
            });
    });
    api.MapGet("/runs/{id:guid}/collaboration", async Task<Results<
        Ok<CollaborationSynthesisArtifactResponse[]>, NotFound>> (
        Guid id, CollaboratorDb db, CancellationToken ct) =>
    {
        if (!await db.Runs.AnyAsync(x => x.Id == id, ct))
        {
            return TypedResults.NotFound();
        }
        var artifact = await db.WorkflowArtifacts.AsNoTracking().SingleOrDefaultAsync(
            x => x.RunId == id && x.Stage == WorkflowStages.Synthesis &&
                x.ArtifactKey == "cross-role-synthesis" && x.Revision == 1 &&
                x.Status == WorkflowArtifactStatuses.Completed,
            ct);
        if (artifact is null)
        {
            return TypedResults.Ok(Array.Empty<CollaborationSynthesisArtifactResponse>());
        }
        var synthesis = JsonSerializer.Deserialize<CollaborationSynthesis>(
            artifact.PayloadJson, JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Persisted collaboration synthesis is invalid.");
        var skillRevisions = JsonSerializer.Deserialize<AgentSkillRevisionReference[]>(
            artifact.SkillRevisionsJson, JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Persisted collaboration skill provenance is invalid.");
        return TypedResults.Ok(new[]
        {
            new CollaborationSynthesisArtifactResponse(
                artifact.Revision,
                artifact.Status,
                synthesis,
                artifact.InputArtifactRevisionsJson,
                skillRevisions,
                artifact.RepositoryCommit,
                artifact.CreatedAt)
        });
    })
        .WithName("GetRunCollaboration")
        .WithSummary("Get cross-role collaboration synthesis for a feature run")
        .WithDescription(
            "Returns source-linked agreements, conflicts, and open questions from the captured developer skill analyses. The synthesis is advisory and may be empty while processing or for legacy runs.")
        .Produces<CollaborationSynthesisArtifactResponse[]>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status404NotFound);
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
    var assignments = await db.ReviewAssignments.AsNoTracking()
        .Where(x => x.RunId == id)
        .OrderBy(x => x.Revision).ThenBy(x => x.DeveloperId)
        .ToListAsync(ct);
    return Results.Ok(assignments.Select(assignment => new
    {
        assignment.RunId,
        assignment.Revision,
        UserId = assignment.DeveloperId,
        assignment.DeveloperDisplayName,
        assignment.ReviewFocus,
        matchedRoles = JsonSerializer.Deserialize<string[]>(
            assignment.MatchedRolesJson, JsonSerializerOptions.Web) ?? [],
        assignment.ReviewPrompt,
        assignment.Decision,
        assignment.Response,
        status = assignment.RespondedAt == null ? "pending" : "completed",
        assignment.AssignedAt,
        assignment.RespondedAt
    }));
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
    var response = request.Response?.Trim();
    if (assignment.RespondedAt is not null)
    {
        return assignment.Decision == request.Decision && assignment.Response == response
            ? Results.Accepted($"/api/v1/runs/{id}/assignments")
            : Results.Problem(statusCode: 409, title: "This developer has already responded to the assignment.");
    }
    if (run.Status != "awaiting_approval")
    {
        return Results.Problem(statusCode: 409, title: "The run is not awaiting review for this plan revision.");
    }
    assignment.Decision = request.Decision;
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
    var reviews = await db.ReviewAssignments.AsNoTracking()
        .Where(x => x.RunId == id && x.Revision == decision.Revision)
        .Select(x => new { x.Decision, x.RespondedAt })
        .ToListAsync(ct);
    if (reviews.Count != run.ReviewerCount || reviews.Any(x => x.RespondedAt is null || x.Decision is null))
    {
        return Results.Problem(statusCode: 409,
            title: $"All {run.ReviewerCount} selected developers must submit a review before a plan decision.");
    }
    if (decision.Decision == PlanDecisionKinds.Approve &&
        reviews.Any(x => x.Decision != DeveloperReviewDecisionKinds.Approve))
    {
        return Results.Problem(statusCode: 409,
            title: "The plan cannot be approved until every selected developer approves the same revision.");
    }
    if (decision.Decision == PlanDecisionKinds.Revise && run.PlanRevision >= InputRules.MaximumPlanRevisions)
    {
        return Results.Problem(statusCode: 409,
            title: $"The revision limit of {InputRules.MaximumPlanRevisions} has been reached; approve or reject this plan.");
    }

    var actor = string.IsNullOrWhiteSpace(decision.ActorId) ? "development-api-key" : decision.ActorId.Trim();
    if (decision.ActorId is not null &&
        !await db.Developers.AsNoTracking().AnyAsync(x => x.UserId == actor && x.IsEnabled, ct))
    {
        return Results.Problem(statusCode: 403, title: "The supplied developer actor is not configured and enabled.");
    }
    var comment = string.IsNullOrWhiteSpace(decision.Comment) ? null : decision.Comment.Trim();
    run.Decision = decision.Decision;
    run.DecisionComment = comment;
    run.DecisionBy = actor;
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

static IResult SubmissionResult(FeatureRun run, string text, string analystId, string skillId, int reviewerCount) =>
    run.Text != text || run.BusinessAnalystId != analystId ||
    run.BusinessAnalystSkillId != skillId || run.ReviewerCount != reviewerCount
        ? Results.Problem(statusCode: 409, title: "Idempotency key was already used with different request details.")
        : Results.Accepted($"/api/v1/runs/{run.Id}", new
        {
            featureRequestId = run.Id, runId = run.Id, run.Status,
            statusUrl = $"/api/v1/runs/{run.Id}"
        });

static async Task<IResult> CreateAgentSkillRevisionAsync(
    string roleId,
    string domain,
    string skillId,
    AgentSkillRevisionRequest request,
    CollaboratorDb db,
    CancellationToken cancellationToken)
{
    if (!InputRules.IsValidAgentSkillRevision(roleId, domain, skillId, request, out var error))
    {
        return Results.Problem(statusCode: 400, title: error);
    }

    var skill = await db.AgentSkills.SingleOrDefaultAsync(
        x => x.RoleId == roleId && x.Domain == domain && x.SkillId == skillId,
        cancellationToken);
    var latestRevision = await db.AgentSkillRevisions
        .Where(x => x.RoleId == roleId && x.Domain == domain && x.SkillId == skillId)
        .Select(x => (int?)x.Revision)
        .MaxAsync(cancellationToken) ?? 0;
    var revision = latestRevision + 1;
    var now = DateTimeOffset.UtcNow;
    var name = request.Name.Trim();
    var prompt = request.Prompt.Trim();
    var actorId = string.IsNullOrWhiteSpace(request.ActorId) ? null : request.ActorId.Trim();
    var changeReason = string.IsNullOrWhiteSpace(request.ChangeReason) ? null : request.ChangeReason.Trim();

    if (skill is null)
    {
        skill = new AgentSkillRecord
        {
            RoleId = roleId,
            Domain = domain,
            SkillId = skillId
        };
        db.AgentSkills.Add(skill);
    }
    skill.ActiveRevision = revision;
    skill.IsEnabled = request.IsEnabled;
    skill.UpdatedAt = now;
    db.AgentSkillRevisions.Add(new AgentSkillRevisionRecord
    {
        RoleId = roleId,
        Domain = domain,
        SkillId = skillId,
        Revision = revision,
        Name = name,
        Prompt = prompt,
        CreatedBy = actorId,
        ChangeReason = changeReason,
        CreatedAt = now
    });

    try
    {
        await db.SaveChangesAsync(cancellationToken);
    }
    catch (DbUpdateConcurrencyException)
    {
        return Results.Problem(statusCode: 409,
            title: "The active skill revision changed concurrently; reload before creating another revision.");
    }
    catch (DbUpdateException ex) when (
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
    {
        return Results.Problem(statusCode: 409,
            title: "The skill or revision was created concurrently; reload the skill history.");
    }

    return Results.Created(
        $"/api/v1/agent-skills/{roleId}/{domain}/{skillId}/revisions",
        new
        {
            skill.RoleId,
            skill.Domain,
            skill.SkillId,
            revision,
            name,
            prompt,
            skill.IsEnabled,
            actorId,
            changeReason,
            createdAt = now
        });
}
