using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using Collaborator.Api;
using Collaborator.Agents;
using Collaborator.Core;
using Collaborator.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Collaborator.Tests;

[TestClass]
public sealed class DeveloperProposalTests
{
    private static readonly DeveloperReviewerMatch[] Matches =
        [new("alice", ["backend"]), new("bob", ["testing"])];
    private static readonly DraftPlan Plan = new("Export tasks", ["Implement CSV download"], []);

    [TestMethod]
    [DataRow(null, true)]
    [DataRow("proposals-first", true)]
    [DataRow("draft-first", true)]
    [DataRow("invalid", false)]
    public void ValidatesPlanningMode(string? mode, bool expected)
    {
        Assert.AreEqual(expected, InputRules.IsValidFeatureSubmission(new("Export tasks", PlanningMode: mode), out _));
    }

    [TestMethod]
    [DataRow(1, "Implement export", false, true)]
    [DataRow(1, null, true, true)]
    [DataRow(1, "", false, false)]
    [DataRow(1, "Conflicting input", true, false)]
    [DataRow(0, "Approach", false, false)]
    [DataRow(6, "Approach", false, false)]
    public void RequiresProposalOrExplicitNoInput(int revision, string? text, bool noInput, bool expected)
    {
        Assert.AreEqual(expected, DeveloperProposalRules.IsValid(new(revision, text, noInput)));
        Assert.IsFalse(DeveloperProposalRules.IsValid(new(1, new string('a', 4001))));
        Assert.IsFalse(DeveloperProposalRules.IsValid(new(1, new string(' ', 4001), true)));
    }

    [TestMethod]
    public async Task AllDevelopersMustContributeBeforeGenerationAndRoundFreezes()
    {
        await using var db = await DatabaseAsync();
        var run = await db.Runs.SingleAsync();
        var store = new RunStore(db);
        store.ConfigurePlanning(run.Id, DeveloperPlanningModes.ProposalsFirst);
        await db.SaveChangesAsync();
        await store.PrepareProposalRoundAsync(run.Id, 1, Matches, default);
        await store.PrepareProposalRoundAsync(run.Id, 1, Matches, default);
        Assert.HasCount(1, await store.GetProposalRoundsAsync(run.Id, default));
        Assert.AreEqual("awaiting_developer_proposals", run.Status);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            store.GetDeveloperProposalsForPlanAsync(run.Id, 1, default));
        await store.SubmitDeveloperProposalAsync(run.Id, "alice", new(1, "Use the existing export service"), default);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            store.GetDeveloperProposalsForPlanAsync(run.Id, 1, default));
        await store.SubmitDeveloperProposalAsync(run.Id, "alice", new(1, "Add streaming and Unicode tests"), default);
        await store.SubmitDeveloperProposalAsync(run.Id, "bob", new(1, null, true), default);
        var inputs = await store.GetDeveloperProposalsForPlanAsync(run.Id, 1, default);
        Assert.IsNotNull(inputs);
        Assert.HasCount(2, inputs);
        Assert.AreEqual("Add streaming and Unicode tests", inputs[0].Proposal);
        Assert.IsTrue(inputs[1].NoAdditionalInput);
        Assert.AreEqual("developer_proposals_ready", run.Status);
        await store.SubmitDeveloperProposalAsync(run.Id, "bob", new(1, null, true), default);
        Assert.AreEqual(1, await db.Outbox.CountAsync(x => x.Kind == "proposals-ready" && x.Revision == 1));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            store.SubmitDeveloperProposalAsync(run.Id, "alice", new(1, "Late modification"), default));
    }

    [TestMethod]
    public async Task UnassignedStaleAndInvalidContributionsAreRejected()
    {
        await using var db = await DatabaseAsync();
        var run = await db.Runs.SingleAsync();
        var store = new RunStore(db);
        store.ConfigurePlanning(run.Id, DeveloperPlanningModes.ProposalsFirst);
        await db.SaveChangesAsync();
        await store.PrepareProposalRoundAsync(run.Id, 1, Matches, default);
        await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() =>
            store.SubmitDeveloperProposalAsync(run.Id, "mallory", new(1, "My idea"), default));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            store.SubmitDeveloperProposalAsync(run.Id, "alice", new(2, "Stale round"), default));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            store.SubmitDeveloperProposalAsync(run.Id, "alice", new(1, null), default));
    }

    [TestMethod]
    [DataRow("proposals-first")]
    [DataRow("draft-first")]
    public async Task BothModesGateCombinedPlansAndReconfirmEveryoneOnRevision(string mode)
    {
        await using var db = await DatabaseAsync();
        var run = await db.Runs.SingleAsync();
        var store = new RunStore(db);
        store.ConfigurePlanning(run.Id, mode);
        await db.SaveChangesAsync();
        await store.CompleteDeveloperSkillAnalysesAsync(run.Id, default);
        var target = 1;
        if (mode == DeveloperPlanningModes.DraftFirst)
        {
            await store.StorePlanAsync(run.Id, 1, Plan, default, Matches);
            Assert.AreEqual("initial_plan_drafted", run.Status);
            Assert.AreEqual(0, await db.ReviewAssignments.CountAsync());
            target = 2;
            Assert.IsNotNull((await store.GetRevisionContextAsync(run.Id, 2, default)).PreviousPlan);
        }
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            store.GetDeveloperProposalsForPlanAsync(run.Id, target, default));
        var oldRevision = run.PlanRevision;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            store.StorePlanAsync(run.Id, target, Plan, default, Matches));
        Assert.AreEqual(oldRevision, run.PlanRevision);
        await store.PrepareProposalRoundAsync(run.Id, target, Matches, default);
        await store.SubmitDeveloperProposalAsync(run.Id, "alice", new(target, "Reuse CSV helper"), default);
        await store.SubmitDeveloperProposalAsync(run.Id, "bob", new(target, null, true), default);
        await store.StorePlanAsync(run.Id, target, Plan, default, Matches);
        Assert.AreEqual("awaiting_approval", run.Status);
        Assert.HasCount(2, await db.ReviewAssignments.Where(x => x.Revision == target).ToArrayAsync());
        var previous = await db.PlanRevisions.SingleAsync(x => x.Revision == target);
        previous.Decision = PlanDecisionKinds.Revise;
        previous.Comment = "Add error handling";
        run.Decision = PlanDecisionKinds.Revise;
        await db.SaveChangesAsync();
        await store.PrepareProposalRoundAsync(run.Id, target + 1, Matches, default);
        var next = (await store.GetProposalRoundsAsync(run.Id, default)).Last();
        Assert.IsTrue(next.Developers.All(x => x.SubmittedAt is null));
        Assert.AreEqual("Reuse CSV helper", next.Developers[0].Proposal);
        Assert.IsTrue(next.Developers[1].NoAdditionalInput);
        await store.SubmitDeveloperProposalAsync(run.Id, "alice", new(target + 1, "Reuse CSV helper with retries"), default);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            store.GetDeveloperProposalsForPlanAsync(run.Id, target + 1, default));
        await store.SubmitDeveloperProposalAsync(run.Id, "bob", new(target + 1, null, true), default);
        var confirmed = await store.GetDeveloperProposalsForPlanAsync(run.Id, target + 1, default);
        Assert.AreEqual("Reuse CSV helper with retries", confirmed![0].Proposal);
        var oldRound = (await store.GetProposalRoundsAsync(run.Id, default)).First();
        Assert.AreEqual("Reuse CSV helper", oldRound.Developers[0].Proposal);
        await store.StorePlanAsync(run.Id, target + 1, Plan, default, Matches);
        Assert.IsTrue(await db.ReviewAssignments.Where(x => x.Revision == target + 1).AllAsync(x => x.Decision == null));
    }

    [TestMethod]
    public async Task LegacyRunsDoNotRequireNewInput()
    {
        await using var db = await DatabaseAsync();
        var run = await db.Runs.SingleAsync();
        var store = new RunStore(db);
        Assert.IsNull(await store.GetPlanningModeAsync(run.Id, default));
        Assert.IsNull(await store.GetDeveloperProposalsForPlanAsync(run.Id, 1, default));
        await store.CompleteDeveloperSkillAnalysesAsync(run.Id, default);
        await store.StorePlanAsync(run.Id, 1, Plan, default, Matches);
        Assert.AreEqual("awaiting_approval", run.Status);
    }

    [TestMethod]
    public async Task PlannerReceivesEveryConfirmedProposalAndExplicitNoInput()
    {
        var generator = new CapturingGenerator();
        var proposals = new DeveloperProposal[]
        {
            new("alice", "Alice", ["backend"], null, "Use streaming CSV", false, DateTimeOffset.UtcNow),
            new("bob", "Bob", ["testing"], null, null, true, DateTimeOffset.UtcNow)
        };
        await new Planner(generator).CreateAsync("Brief", "Feature", null, null, null, 1,
            null, null, default, developerProposals: proposals);
        using var document = JsonDocument.Parse(generator.Input!);
        var inputs = document.RootElement.GetProperty("developerProposals");
        Assert.AreEqual(2, inputs.GetArrayLength());
        Assert.AreEqual("Use streaming CSV", inputs[0].GetProperty("proposal").GetString());
        Assert.IsTrue(inputs[1].GetProperty("noAdditionalInput").GetBoolean());
    }

    [TestMethod]
    public async Task HttpRoutesEnforceRoundMembershipAndExplicitInputAndPersistAllContributions()
    {
        var name = Guid.NewGuid().ToString();
        Guid id;
        await using (var db = await DatabaseAsync(name))
        {
            id = (await db.Runs.SingleAsync()).Id;
            var store = new RunStore(db);
            store.ConfigurePlanning(id, DeveloperPlanningModes.ProposalsFirst);
            await db.SaveChangesAsync();
            await store.PrepareProposalRoundAsync(id, 1, Matches, default);
        }
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDbContext<CollaboratorDb>(options => options.UseInMemoryDatabase(name));
        builder.Services.AddScoped<RunStore>();
        await using var app = builder.Build();
        app.MapGroup("/api/v1").MapDeveloperProposalEndpoints();
        await app.StartAsync();
        using var client = new HttpClient
        {
            BaseAddress = new Uri(app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single())
        };
        var path = $"/api/v1/runs/{id}/developer-proposals";
        using var invalid = await client.PostAsJsonAsync($"{path}/alice", new DeveloperProposalSubmission(1, ""));
        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var unassigned = await client.PostAsJsonAsync($"{path}/mallory", new DeveloperProposalSubmission(1, "Idea"));
        Assert.AreEqual(HttpStatusCode.NotFound, unassigned.StatusCode);
        using var stale = await client.PostAsJsonAsync($"{path}/alice", new DeveloperProposalSubmission(2, "Idea"));
        Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
        using var first = await client.PostAsJsonAsync($"{path}/alice", new DeveloperProposalSubmission(1, "Use streaming"));
        Assert.AreEqual(HttpStatusCode.Accepted, first.StatusCode);
        var partial = await client.GetFromJsonAsync<DeveloperProposalRound[]>(path);
        Assert.AreEqual("collecting", partial![0].Status);
        using var second = await client.PostAsJsonAsync($"{path}/bob", new DeveloperProposalSubmission(1, null, true));
        Assert.AreEqual(HttpStatusCode.Accepted, second.StatusCode);
        var complete = await client.GetFromJsonAsync<DeveloperProposalRound[]>(path);
        Assert.AreEqual("ready", complete![0].Status);
        Assert.AreEqual("Use streaming", complete[0].Developers[0].Proposal);
        using var closed = await client.PostAsJsonAsync($"{path}/alice", new DeveloperProposalSubmission(1, "Change frozen round"));
        Assert.AreEqual(HttpStatusCode.Conflict, closed.StatusCode);
        using var missing = await client.GetAsync($"/api/v1/runs/{Guid.NewGuid()}/developer-proposals");
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private sealed class CapturingGenerator : IPlanGenerator
    {
        public string? Input { get; private set; }
        public Task<DraftPlan> GenerateAsync(string input, CancellationToken cancellationToken)
        {
            Input = input;
            return Task.FromResult(Plan);
        }
    }

    private static async Task<CollaboratorDb> DatabaseAsync(string? name = null)
    {
        var db = new CollaboratorDb(new DbContextOptionsBuilder<CollaboratorDb>()
            .UseInMemoryDatabase(name ?? Guid.NewGuid().ToString()).Options);
        var id = Guid.NewGuid();
        db.Runs.Add(new FeatureRun
        {
            Id = id, ProductId = "sample", Text = "Feature", ProductBrief = "Brief", IdempotencyKey = "key",
            BusinessAnalystId = "ba", BusinessAnalystSkillId = "analysis", BusinessAnalystSkillName = "Analysis",
            BusinessAnalystPrompt = "Clarify", BusinessAnalystApproved = true, FeatureSpecificationRevision = 1,
            RepositoryCommit = "commit", ReviewerCount = 2
        });
        db.Developers.AddRange(
            new DeveloperRecord { UserId = "alice", DisplayName = "Alice", RolesJson = """["backend"]""" },
            new DeveloperRecord { UserId = "bob", DisplayName = "Bob", RolesJson = """["testing"]""" });
        db.FeatureSpecificationRevisions.Add(new FeatureSpecificationRevisionRecord
        {
            RunId = id, Revision = 1, SpecificationJson = "{}", Decision = BusinessAnalystDecisionKinds.Approve
        });
        db.WorkflowArtifacts.Add(new WorkflowArtifactRecord
        {
            RunId = id, Stage = WorkflowStages.Architecture, ArtifactKey = "architecture-analysis",
            Revision = 1, ArtifactType = nameof(ArchitectureAnalysis), PayloadJson = "{}",
            Status = WorkflowArtifactStatuses.Approved, Decision = ArchitectureAnalysisDecisionKinds.Approve,
            RepositoryCommit = "commit"
        });
        await db.SaveChangesAsync();
        return db;
    }
}
