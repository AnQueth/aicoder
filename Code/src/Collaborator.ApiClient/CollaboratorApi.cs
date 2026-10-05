using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Collaborator.Core;

namespace Collaborator.ApiClient;

internal sealed record RunView(
    Guid Id, string ProductId, string Text, string Status, DateTimeOffset CreatedAt,
    int PlanRevision, bool? Approved, string? Decision, string? DecisionComment,
    string? FailureCode, string? RepositoryCommit, RepositoryCatalog? RepositoryCatalog, DraftPlan? Plan,
    CodeChangeStatus? CodeChange);

internal sealed record RunEventView(long Id, Guid RunId, string Kind, string Actor, DateTimeOffset CreatedAt);

internal sealed record PlanRevisionView(
    int Revision, DateTimeOffset CreatedAt, string? Decision, string? Comment,
    string? DecidedBy, DateTimeOffset? DecidedAt, DraftPlan? Plan);

internal sealed record ReviewAssignmentView(
    Guid RunId, int Revision, string UserId, string DeveloperDisplayName, string? ReviewFocus,
    string ReviewPrompt, string? Response, string Status, DateTimeOffset AssignedAt, DateTimeOffset? RespondedAt);

internal sealed record SubmissionView(Guid RunId, string Status, string StatusUrl);

/// <summary>Outcome of an API call: status code plus either a typed body or the problem title.</summary>
internal sealed record ApiResult<T>(HttpStatusCode Status, T? Body, string? Problem)
{
    public bool IsSuccess => (int)Status is >= 200 and < 300;
}

/// <summary>Thin typed wrapper over the Collaborator REST API (all routes under /api/v1).</summary>
internal sealed class CollaboratorApi(HttpClient http, Action<string>? trace)
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public int Requests { get; private set; }
    public int NonSuccessResponses { get; private set; }

    public async Task<bool> HealthAsync(CancellationToken ct)
    {
        Requests++;
        using var response = await http.GetAsync("/health", ct);
        trace?.Invoke($"GET /health -> {(int)response.StatusCode}");
        if (!response.IsSuccessStatusCode)
        {
            NonSuccessResponses++;
        }
        return response.IsSuccessStatusCode;
    }

    public Task<ApiResult<JsonElement>> RegisterProductAsync(ProductRegistration product, CancellationToken ct) =>
        SendAsync<JsonElement>(HttpMethod.Post, "/api/v1/products", product, null, ct);

    public Task<ApiResult<JsonElement>> ConfigureDeveloperAsync(
        string userId, DeveloperConfiguration developer, CancellationToken ct) =>
        SendAsync<JsonElement>(HttpMethod.Put,
            $"/api/v1/developers/{Uri.EscapeDataString(userId)}", developer, null, ct);

    public Task<ApiResult<JsonElement>> UpdateProductRepositoryAsync(
        string productId, string? repositoryUrl, CancellationToken ct) =>
        SendAsync<JsonElement>(HttpMethod.Put,
            $"/api/v1/products/{Uri.EscapeDataString(productId)}/repository",
            new RepositorySourceUpdate(repositoryUrl), null, ct);

    public Task<ApiResult<SubmissionView>> SubmitFeatureAsync(
        string productId, string text, string idempotencyKey, CancellationToken ct) =>
        SendAsync<SubmissionView>(HttpMethod.Post, $"/api/v1/products/{Uri.EscapeDataString(productId)}/feature-requests",
            new FeatureSubmission(text), idempotencyKey, ct);

    public Task<ApiResult<RunView>> GetRunAsync(Guid runId, CancellationToken ct) =>
        SendAsync<RunView>(HttpMethod.Get, $"/api/v1/runs/{runId}", null, null, ct);

    public Task<ApiResult<RunEventView[]>> GetEventsAsync(Guid runId, long after, CancellationToken ct) =>
        SendAsync<RunEventView[]>(HttpMethod.Get, $"/api/v1/runs/{runId}/events?after={after}", null, null, ct);

    public Task<ApiResult<PlanRevisionView[]>> GetPlansAsync(Guid runId, CancellationToken ct) =>
        SendAsync<PlanRevisionView[]>(HttpMethod.Get, $"/api/v1/runs/{runId}/plans", null, null, ct);

    public Task<ApiResult<ReviewAssignmentView[]>> GetAssignmentsAsync(Guid runId, CancellationToken ct) =>
        SendAsync<ReviewAssignmentView[]>(HttpMethod.Get, $"/api/v1/runs/{runId}/assignments", null, null, ct);

    public Task<ApiResult<JsonElement>> RespondToAssignmentAsync(
        Guid runId, string userId, ReviewAssignmentResponse response, CancellationToken ct) =>
        SendAsync<JsonElement>(HttpMethod.Post,
            $"/api/v1/runs/{runId}/assignments/{Uri.EscapeDataString(userId)}/response", response, null, ct);

    public Task<ApiResult<JsonElement>> DecideAsync(Guid runId, PlanDecision decision, CancellationToken ct) =>
        SendAsync<JsonElement>(HttpMethod.Post, $"/api/v1/runs/{runId}/plan-decisions", decision, null, ct);

    private async Task<ApiResult<T>> SendAsync<T>(
        HttpMethod method, string path, object? body, string? idempotencyKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }
        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }
        Requests++;
        using var response = await http.SendAsync(request, ct);
        trace?.Invoke($"{method} {path} -> {(int)response.StatusCode}");
        var content = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            NonSuccessResponses++;
            return new(response.StatusCode, default, ProblemTitle(content) ?? response.ReasonPhrase);
        }
        if (string.IsNullOrWhiteSpace(content))
        {
            return new(response.StatusCode, default, null);
        }
        return new(response.StatusCode, JsonSerializer.Deserialize<T>(content, Json), null);
    }

    private static string? ProblemTitle(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(content);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("title", out var title) ? title.GetString() : content;
        }
        catch (JsonException)
        {
            return content.Length > 200 ? content[..200] : content;
        }
    }
}
