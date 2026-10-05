using System.Diagnostics;
using System.Net;
using Collaborator.ApiClient;
using Collaborator.Core;

ClientOptions options;
try
{
    options = ClientOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 2;
}
if (options.ShowHelp)
{
    Console.WriteLine(ClientOptions.Usage);
    return 0;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var runner = new FlowRunner(options);
try
{
    return await runner.RunAsync(cts.Token);
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    runner.PrintSummary("cancelled");
    return 130;
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"error: HTTP request failed: {ex.Message}");
    Console.Error.WriteLine($"       Is the API running at {options.BaseUrl}? Start it with: aspire run (or dotnet run --project src/Collaborator.AppHost)");
    runner.PrintSummary("failed");
    return 1;
}

namespace Collaborator.ApiClient
{
    /// <summary>Drives one feature request through review and reports its human-review branch.</summary>
    internal sealed class FlowRunner(ClientOptions options)
    {
        private const int TotalPhases = 6;
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);

        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private readonly List<string> decisions = [];
        private readonly HashSet<string> reviewResponsesSubmitted = new(StringComparer.Ordinal);
        private CollaboratorApi? api;
        private Guid? runId;
        private long lastEventId;
        private int revisionsSeen;
        private string? finalStatus;

        public async Task<int> RunAsync(CancellationToken ct)
        {
            var key = options.ResolveApiKey(out var keySource);
            if (key is null || key.Length < 32)
            {
                Console.Error.WriteLine("error: no API key (>= 32 chars) found. Pass --api-key, set COLLABORATOR_API_KEY,");
                Console.Error.WriteLine("       or run the AppHost once so its user secrets contain Parameters:api-key.");
                return 2;
            }

            using var http = new HttpClient { BaseAddress = options.BaseUrl, Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.Add("X-Api-Key", key);
            api = new CollaboratorApi(http, options.Verbose ? line => Log($"  http  {line}") : null);
            Info($"API {options.BaseUrl} (key from {keySource}); mode: {(options.Interactive ? "interactive" : "scripted")}");

            Phase(1, "Health check");
            if (!await api.HealthAsync(ct))
            {
                return Fail("API health check failed.");
            }
            Info("  healthy");

            Guid id;
            if (options.ExistingRunId is { } existing)
            {
                Phase(2, "Product registration (skipped: attaching to existing run)");
                if (!await ConfigureDevelopersAsync(ct))
                {
                    return 1;
                }
                Phase(3, $"Attach to run {existing}");
                id = existing;
            }
            else
            {
                Phase(2, $"Register product '{options.ProductId}'");
                if (!await ConfigureDevelopersAsync(ct))
                {
                    return 1;
                }
                var product = await api.RegisterProductAsync(
                    new ProductRegistration(
                        options.ProductId, options.ProductName, options.ProductBrief, options.ProductRepositoryUrl), ct);
                if (product.Status == HttpStatusCode.Conflict)
                {
                    Info("  already registered (409) - reusing");
                    if (options.ProductRepositoryUrl is not null)
                    {
                        var updated = await api.UpdateProductRepositoryAsync(
                            options.ProductId, options.ProductRepositoryUrl, ct);
                        if (!updated.IsSuccess)
                        {
                            return Fail($"repository configuration returned {(int)updated.Status}: {updated.Problem}");
                        }
                        Info("  repository source configured");
                    }
                }
                else if (!product.IsSuccess)
                {
                    return Fail($"product registration returned {(int)product.Status}: {product.Problem}");
                }
                else
                {
                    Info("  created (201)");
                    if (options.ProductRepositoryUrl is not null)
                    {
                        Info("  repository source configured");
                    }
                }

                Phase(3, "Submit feature request");
                var feature = options.Feature ?? (options.Interactive ? Prompt("Feature request text") : null);
                if (string.IsNullOrWhiteSpace(feature))
                {
                    return Fail("feature text is required (--feature).");
                }
                var idempotencyKey = Guid.NewGuid().ToString("N");
                var submitted = await api.SubmitFeatureAsync(options.ProductId, feature, idempotencyKey, ct);
                if (!submitted.IsSuccess || submitted.Body is null)
                {
                    return Fail($"submission returned {(int)submitted.Status}: {submitted.Problem}");
                }
                id = submitted.Body.RunId;
                Info($"  accepted (202) run {id} status={submitted.Body.Status} idempotency-key={idempotencyKey}");
            }
            runId = id;

            Phase(4, "Plan review loop");
            var run = await WaitForAsync(id, r => r.Status == "awaiting_approval" || IsTerminal(r.Status),
                "first plan", ct);
            if (run is null)
            {
                return 1;
            }

            var feedback = new Queue<string>(options.Feedback);
            while (run.Status == "awaiting_approval")
            {
                revisionsSeen = Math.Max(revisionsSeen, run.PlanRevision);
                PrintPlan(run);
                if (!await ReviewAssignmentsAsync(id, run.PlanRevision, ct))
                {
                    return Fail("could not retrieve or submit review assignment responses.");
                }
                var decision = options.Interactive ? AskDecision(run) : NextScriptedDecision(run, feedback);
                var posted = await api.DecideAsync(id, decision, ct);
                if (!posted.IsSuccess)
                {
                    Warn($"  decision rejected by API ({(int)posted.Status}): {posted.Problem}");
                    if (options.Interactive)
                    {
                        continue;
                    }
                    return Fail("scripted decision was not accepted.");
                }
                decisions.Add($"r{decision.Revision}:{decision.Decision}");
                Info($"  decision '{decision.Decision}' accepted for revision {decision.Revision}");

                var revision = run.PlanRevision;
                run = decision.Decision == PlanDecisionKinds.Revise
                    ? await WaitForAsync(id, r => (r.PlanRevision > revision && r.Status == "awaiting_approval")
                        || IsTerminal(r.Status), $"revision {revision + 1}", ct)
                    : await WaitForAsync(id, r => IsTerminal(r.Status), "final status", ct);
                if (run is null)
                {
                    return 1;
                }
            }

            Phase(5, "Outcome and review branch");
            finalStatus = run.Status;
            Info($"  run {id}: {run.Status}{(run.FailureCode is null ? "" : $" ({run.FailureCode})")}");
            if (run.CodeChange is { } change)
            {
                Info($"  code change: {change.Status}");
                if (change.BranchName is not null && change.Commit is not null)
                {
                    Info($"  review branch: {change.BranchName}");
                    Info($"  commit: {change.Commit}");
                    Info($"  changed files: {string.Join(", ", change.ChangedFiles)}");
                    if (!options.Quiet && change.Summary is not null)
                    {
                        Info($"  summary: {change.Summary}");
                    }
                }
            }

            Phase(6, "Plan history and events");
            await PrintHistoryAsync(id, ct);
            PrintSummary(run.Status);
            return run.Status is "code_change_ready" or "plan_approved" or "plan_rejected" ? 0 : 1;
        }

        public void PrintSummary(string outcome)
        {
            Console.WriteLine();
            Console.WriteLine("=== Summary ===");
            Console.WriteLine($"  outcome         : {outcome}");
            Console.WriteLine($"  run             : {(runId?.ToString() ?? "(none)")}");
            Console.WriteLine($"  final status    : {finalStatus ?? "(unknown)"}");
            Console.WriteLine($"  plan revisions  : {revisionsSeen}");
            Console.WriteLine($"  decisions       : {(decisions.Count == 0 ? "(none)" : string.Join(", ", decisions))}");
            Console.WriteLine($"  http requests   : {api?.Requests ?? 0} ({api?.NonSuccessResponses ?? 0} non-2xx, incl. expected 409s)");
            Console.WriteLine($"  elapsed         : {elapsed.Elapsed.TotalSeconds:F1}s");
        }

        private PlanDecision NextScriptedDecision(RunView run, Queue<string> feedback)
        {
            if (feedback.TryDequeue(out var comment))
            {
                Info($"  [scripted] revise: {comment}");
                return new(run.PlanRevision, PlanDecisionKinds.Revise, comment);
            }
            if (options.FinalDecision == "reject")
            {
                Info($"  [scripted] reject: {options.RejectReason}");
                return new(run.PlanRevision, PlanDecisionKinds.Reject, options.RejectReason);
            }
            Info("  [scripted] approve");
            return new(run.PlanRevision, PlanDecisionKinds.Approve);
        }

        private static PlanDecision AskDecision(RunView run)
        {
            while (true)
            {
                var remaining = InputRules.MaximumPlanRevisions - run.PlanRevision;
                Console.Write($"Decision for revision {run.PlanRevision} - [a]pprove, [r]eject, [f]eedback/revise ({remaining} revisions left): ");
                var choice = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
                switch (choice)
                {
                    case "a" or "approve":
                        return new(run.PlanRevision, PlanDecisionKinds.Approve);
                    case "r" or "reject":
                        var reason = Prompt("Rejection reason");
                        if (!string.IsNullOrWhiteSpace(reason))
                        {
                            return new(run.PlanRevision, PlanDecisionKinds.Reject, reason);
                        }
                        break;
                    case "f" or "feedback" or "revise":
                        var comment = Prompt("Feedback for the AI planner");
                        if (!string.IsNullOrWhiteSpace(comment))
                        {
                            return new(run.PlanRevision, PlanDecisionKinds.Revise, comment);
                        }
                        break;
                }
                Console.WriteLine("  Please choose a, r, or f (reject and feedback need text).");
            }
        }

        private async Task<RunView?> WaitForAsync(Guid id, Func<RunView, bool> done, string what, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.Timeout);
            var waitClock = Stopwatch.StartNew();
            var lastBeat = TimeSpan.Zero;
            var polls = 0;
            Info($"  waiting for {what} (timeout {options.Timeout.TotalSeconds:F0}s)...");
            try
            {
                while (true)
                {
                    polls++;
                    var result = await api!.GetRunAsync(id, timeout.Token);
                    if (!result.IsSuccess || result.Body is null)
                    {
                        Fail($"GET run returned {(int)result.Status}: {result.Problem}");
                        return null;
                    }
                    var run = result.Body;
                    await PrintNewEventsAsync(id, timeout.Token);
                    if (options.Verbose)
                    {
                        Log($"  poll #{polls} status={run.Status} revision={run.PlanRevision}");
                    }
                    if (run.Status is "failed" or "blocked")
                    {
                        finalStatus = run.Status;
                        Fail($"run ended as {run.Status} ({run.FailureCode ?? "no failure code"}).");
                        return null;
                    }
                    if (done(run))
                    {
                        Info($"  reached status={run.Status} revision={run.PlanRevision} after {waitClock.Elapsed.TotalSeconds:F1}s ({polls} polls)");
                        return run;
                    }
                    if (!options.Quiet && !options.Verbose && waitClock.Elapsed - lastBeat >= HeartbeatInterval)
                    {
                        lastBeat = waitClock.Elapsed;
                        Log($"  ... {waitClock.Elapsed.TotalSeconds:F0}s status={run.Status} revision={run.PlanRevision}");
                    }
                    await Task.Delay(options.PollInterval, timeout.Token);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Fail($"timed out after {options.Timeout.TotalSeconds:F0}s waiting for {what}.");
                return null;
            }
        }

        private async Task PrintNewEventsAsync(Guid id, CancellationToken ct)
        {
            var events = await api!.GetEventsAsync(id, lastEventId, ct);
            foreach (var e in events.Body ?? [])
            {
                lastEventId = Math.Max(lastEventId, e.Id);
                Info($"  event #{e.Id} {e.Kind} by {e.Actor} at {e.CreatedAt:HH:mm:ss}");
            }
        }

        private async Task PrintHistoryAsync(Guid id, CancellationToken ct)
        {
            var plans = await api!.GetPlansAsync(id, ct);
            foreach (var p in plans.Body ?? [])
            {
                var decided = p.Decision is null ? "undecided" : $"{p.Decision} by {p.DecidedBy ?? "?"}";
                Console.WriteLine($"  revision {p.Revision} ({p.CreatedAt:HH:mm:ss}): {decided}{(p.Comment is null ? "" : $" - \"{p.Comment}\"")}");
                if (p.Plan is not null && !options.Quiet)
                {
                    Console.WriteLine($"    {p.Plan.Summary}");
                }
            }
            var assignments = await api.GetAssignmentsAsync(id, ct);
            if (!assignments.IsSuccess || assignments.Body is null)
            {
                Warn($"  assignment history returned {(int)assignments.Status}: {assignments.Problem}");
            }
            else
            {
                Console.WriteLine($"  review assignments ({assignments.Body.Length}):");
                foreach (var assignment in assignments.Body)
                {
                    Console.WriteLine(
                        $"    r{assignment.Revision} {assignment.DeveloperDisplayName} ({assignment.UserId}): {assignment.Status}");
                    if (assignment.Response is not null)
                    {
                        Console.WriteLine($"      response: {assignment.Response}");
                    }
                }
            }
            var all = new List<RunEventView>();
            long after = 0;
            while (true)
            {
                var page = await api.GetEventsAsync(id, after, ct);
                var items = page.Body ?? [];
                all.AddRange(items);
                if (items.Length < 100)
                {
                    break;
                }
                after = items[^1].Id;
            }
            Console.WriteLine($"  events ({all.Count}): {string.Join(" -> ", all.Select(e => e.Kind))}");
        }

        private void PrintPlan(RunView run)
        {
            Console.WriteLine();
            Console.WriteLine($"--- Plan revision {run.PlanRevision} ---");
            if (run.RepositoryCatalog is { } catalog)
            {
                Console.WriteLine($"  Repository catalog: commit {catalog.Commit} ({catalog.FilesAnalyzed} files analyzed)");
                if (!options.Quiet)
                {
                    foreach (var functionality in catalog.Functionalities)
                    {
                        Console.WriteLine($"    - {functionality.Name}: {functionality.Description}");
                        Console.WriteLine($"      Files: {string.Join(", ", functionality.Paths)}");
                        if (functionality.Symbols.Length > 0)
                        {
                            Console.WriteLine($"      Symbols: {string.Join(", ", functionality.Symbols)}");
                        }
                    }
                }
            }
            if (run.Plan is null)
            {
                Console.WriteLine("  (no plan body)");
                return;
            }
            Console.WriteLine($"  Summary: {run.Plan.Summary}");
            Console.WriteLine("  Steps:");
            for (var i = 0; i < run.Plan.Steps.Length; i++)
            {
                Console.WriteLine($"    {i + 1}. {run.Plan.Steps[i]}");
            }
            if (run.Plan.OpenQuestions.Length > 0)
            {
                Console.WriteLine("  Open questions:");
                foreach (var q in run.Plan.OpenQuestions)
                {
                    Console.WriteLine($"    - {q}");
                }
            }
            Console.WriteLine();
        }

        private async Task<bool> ReviewAssignmentsAsync(Guid id, int revision, CancellationToken ct)
        {
            var result = await api!.GetAssignmentsAsync(id, ct);
            if (!result.IsSuccess || result.Body is null)
            {
                Warn($"  review assignments returned {(int)result.Status}: {result.Problem}");
                return false;
            }

            var assignments = result.Body.Where(x => x.Revision == revision).ToArray();
            foreach (var assignment in assignments)
            {
                Info($"  review assignment for {assignment.DeveloperDisplayName} ({assignment.UserId}): {assignment.Status}");
                if (!options.Quiet)
                {
                    Console.WriteLine($"    Prompt: {assignment.ReviewPrompt}");
                    if (assignment.Response is not null)
                    {
                        Console.WriteLine($"    Response: {assignment.Response}");
                    }
                }
                if (assignment.Status != "pending")
                {
                    continue;
                }

                string? response = null;
                if (options.Interactive)
                {
                    response = Prompt($"    Response from {assignment.DeveloperDisplayName} (Enter to skip)");
                }
                else
                {
                    if (!reviewResponsesSubmitted.Contains(assignment.UserId))
                    {
                        options.ReviewResponses.TryGetValue(assignment.UserId, out response);
                    }
                }
                if (string.IsNullOrWhiteSpace(response))
                {
                    continue;
                }

                var posted = await api.RespondToAssignmentAsync(
                    id, assignment.UserId, new ReviewAssignmentResponse(response), ct);
                if (!posted.IsSuccess)
                {
                    Warn($"  response for '{assignment.UserId}' returned {(int)posted.Status}: {posted.Problem}");
                    return false;
                }
                reviewResponsesSubmitted.Add(assignment.UserId);
                Info($"  recorded review response from '{assignment.UserId}'");
            }

            foreach (var userId in options.ReviewResponses.Keys
                .Except(reviewResponsesSubmitted, StringComparer.Ordinal)
                .Except(assignments.Select(x => x.UserId), StringComparer.Ordinal))
            {
                Warn($"  no assignment for developer '{userId}' on revision {revision}; response was not submitted");
            }
            return true;
        }

        private async Task<bool> ConfigureDevelopersAsync(CancellationToken ct)
        {
            foreach (var developer in options.Developers)
            {
                var configured = await api!.ConfigureDeveloperAsync(
                    developer.UserId, developer.Configuration, ct);
                if (!configured.IsSuccess)
                {
                    Warn($"  developer configuration for '{developer.UserId}' returned {(int)configured.Status}: {configured.Problem}");
                    return false;
                }
                Info($"  configured developer '{developer.UserId}'");
            }
            return true;
        }

        private static bool IsTerminal(string status) =>
            status is "code_change_ready" or "plan_approved" or "plan_rejected" or "failed" or "blocked";

        private static string? Prompt(string label)
        {
            Console.Write($"{label}: ");
            return Console.ReadLine()?.Trim();
        }

        private static void Phase(int n, string title) => Console.WriteLine($"\n[{Stamp()}] Phase {n}/{TotalPhases}: {title}");

        private void Info(string message)
        {
            if (!options.Quiet)
            {
                Log(message);
            }
        }

        private static void Log(string message) => Console.WriteLine($"[{Stamp()}] {message}");

        private static void Warn(string message) => Console.Error.WriteLine($"[{Stamp()}] warn: {message.Trim()}");

        private int Fail(string message)
        {
            Console.Error.WriteLine($"[{Stamp()}] error: {message}");
            PrintSummary("failed");
            return 1;
        }

        private static string Stamp() => DateTime.Now.ToString("HH:mm:ss");
    }
}
