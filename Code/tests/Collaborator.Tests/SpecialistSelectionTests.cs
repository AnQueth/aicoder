using System.Net;
using System.Text.Json;
using Collaborator.Agents;
using Collaborator.ApiClient;
using Collaborator.Core;
using Collaborator.Infrastructure;
using Collaborator.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Collaborator.Tests;

[TestClass]
public sealed class SpecialistSelectionTests
{
    private static DeveloperSkillSnapshot Qa => new(
        new("developer-qa", "test-planning", "general", 7), "Custom QA", "Use our test matrix.");
    private static DeveloperSkillSnapshot Backend => new(
        new("developer-backend", "design", "general", 2), "Backend", "Review our service design.");

    [TestMethod]
    public void Resolve_Omitted_UsesEnabledSkillsWithoutChangingPrompts()
    {
        var available = new[] { Qa, Backend };
        Assert.IsTrue(DeveloperSkillSelectionPolicy.TryResolve(available, null, out var selected, out _));
        CollectionAssert.AreEqual(available, selected);
        Assert.AreEqual("Use our test matrix.", selected[0].Prompt);
    }

    [TestMethod]
    public void Resolve_ExplicitSubset_OnlyCapturesSelectedSkillAndRevision()
    {
        Assert.IsTrue(DeveloperSkillSelectionPolicy.TryResolve([Qa, Backend],
            [new("developer-qa", "general", "test-planning")], out var selected, out _));
        Assert.AreEqual(Qa, Assert.ContainsSingle(selected));
    }

    [TestMethod]
    public void Resolve_Empty_SkipsEvenWhenRegistryContainsSkills()
    {
        Assert.IsTrue(DeveloperSkillSelectionPolicy.TryResolve([Qa, Backend], [], out var selected, out _));
        Assert.IsEmpty(selected);
        Assert.IsTrue(DeveloperSkillSelectionPolicy.TryResolve([], null, out selected, out _));
        Assert.IsEmpty(selected);
    }

    [TestMethod]
    public void Resolve_UnavailableSelection_ReportsConflictWithoutPartialSelection()
    {
        Assert.IsFalse(DeveloperSkillSelectionPolicy.TryResolve([Backend],
            [new("developer-qa", "general", "test-planning")], out var selected, out var error));
        Assert.IsEmpty(selected);
        Assert.Contains("developer-qa/general/test-planning", error);
    }

    [TestMethod]
    public void Normalize_OrderIndependentButOmittedAndEmptyDiffer()
    {
        var qa = new DeveloperSkillSelection("developer-qa", "general", "test-planning");
        var backend = new DeveloperSkillSelection("developer-backend", "general", "design");
        CollectionAssert.AreEqual(DeveloperSkillSelectionPolicy.Normalize([qa, backend]),
            DeveloperSkillSelectionPolicy.Normalize([backend, qa]));
        Assert.IsNull(DeveloperSkillSelectionPolicy.Normalize(null));
        Assert.IsEmpty(DeveloperSkillSelectionPolicy.Normalize([])!);
    }

    [TestMethod]
    public void Submission_InvalidOrDuplicateSkillKeys_AreRejected()
    {
        var valid = new DeveloperSkillSelection("developer-qa", "general", "test-planning");
        foreach (var keys in new DeveloperSkillSelection[][]
        {
            [valid, valid], [new("architect", "general", "design")],
            [new("developer-qa", "../general", "test-planning")],
            [new("developer-qa", "general", "")], [null!]
        })
        {
            Assert.IsFalse(InputRules.IsValidFeatureSubmission(
                new FeatureSubmission("Feature", DeveloperSkills: keys), out _));
        }
    }

    [TestMethod]
    public void Parse_SelectionSupportsOmittedEmptyAndSubset()
    {
        Assert.IsNull(ClientOptions.Parse(["--auto"]).DeveloperSkills);
        Assert.IsEmpty(ClientOptions.Parse(["--auto", "--no-specialists"]).DeveloperSkills!);
        var options = ClientOptions.Parse(["--auto",
            "--specialist", "developer-qa/general/test-planning",
            "--specialist", "developer-backend/general/design"]);
        Assert.HasCount(2, options.DeveloperSkills!);
    }

    [TestMethod]
    [DataRow("--no-specialists", "--specialist", "developer-qa/general/test-planning")]
    [DataRow("--specialist", "developer-qa/general/test-planning", "--no-specialists")]
    [DataRow("--specialist", "qa")]
    [DataRow("--specialist", "architect/general/design")]
    [DataRow("--specialist", "developer-qa/general/test-planning", "--specialist", "developer-qa/general/test-planning")]
    [DataRow("--run", "00000000-0000-0000-0000-000000000001", "--no-specialists")]
    public void Parse_InvalidSelection_Throws(params string[] args) =>
        Assert.ThrowsExactly<ArgumentException>(() => ClientOptions.Parse(args));

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task Submit_SendsOmittedEmptyOrSubsetUnchanged(int mode)
    {
        using var handler = new SubmissionHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var api = new CollaboratorApi(http, null);
        DeveloperSkillSelection[]? selection = mode switch
        {
            0 => null,
            1 => [],
            _ => [new("developer-qa", "general", "test-planning")]
        };
        await api.SubmitFeatureAsync("sample", "Feature", "key", "ba", "analysis", 1,
            CancellationToken.None, selection);
        using var body = JsonDocument.Parse(handler.Body!);
        var property = body.RootElement.GetProperty("developerSkills");
        if (mode == 0)
        {
            Assert.AreEqual(JsonValueKind.Null, property.ValueKind);
        }
        else
        {
            Assert.AreEqual(mode - 1, property.GetArrayLength());
            if (mode == 2)
            {
                Assert.AreEqual("developer-qa", property[0].GetProperty("roleId").GetString());
            }
        }
    }

    [TestMethod]
    public async Task EmptySnapshot_CompletesBundleWithoutRequiringAnalysesOrSynthesis()
    {
        var options = new DbContextOptionsBuilder<CollaboratorDb>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new CollaboratorDb(options);
        var id = Guid.NewGuid();
        db.Runs.Add(new FeatureRun
        {
            Id = id, ProductId = "sample", Text = "Feature", ProductBrief = "Brief",
            IdempotencyKey = "key", BusinessAnalystId = "ba", BusinessAnalystSkillId = "analysis",
            BusinessAnalystSkillName = "Analysis", BusinessAnalystPrompt = "Clarify",
            BusinessAnalystApproved = true, FeatureSpecificationRevision = 1,
            RepositoryCommit = "commit", DeveloperSkillsJson = "[]",
            ReviewerCount = 1
        });
        db.Developers.Add(new DeveloperRecord
        {
            UserId = "reviewer", DisplayName = "Reviewer", RolesJson = """["testing"]"""
        });
        db.FeatureSpecificationRevisions.Add(new FeatureSpecificationRevisionRecord
        {
            RunId = id, Revision = 1, SpecificationJson = "{}",
            Decision = BusinessAnalystDecisionKinds.Approve
        });
        db.WorkflowArtifacts.Add(new WorkflowArtifactRecord
        {
            RunId = id, Stage = WorkflowStages.Architecture, ArtifactKey = "architecture-analysis",
            Revision = 1, ArtifactType = nameof(ArchitectureAnalysis), PayloadJson = "{}",
            Status = WorkflowArtifactStatuses.Approved, Decision = ArchitectureAnalysisDecisionKinds.Approve,
            RepositoryCommit = "commit"
        });
        await db.SaveChangesAsync();
        var store = new RunStore(db);

        Assert.IsEmpty(await store.GetDeveloperSkillSnapshotsAsync(id, CancellationToken.None));
        await store.CompleteDeveloperSkillAnalysesAsync(id, CancellationToken.None);
        await store.CompleteDeveloperSkillAnalysesAsync(id, CancellationToken.None);
        var catalog = new RepositoryCatalogService(db, new OfflineRepositoryCatalogGenerator(),
            NullLogger<RepositoryCatalogService>.Instance);
        var workflow = new FeatureWorkflow(
            store, new Planner(new OfflinePlanGenerator()),
            new BusinessAnalyst(new OfflineFeatureSpecificationGenerator()),
            new Architect(new OfflineArchitectureAnalysisGenerator()),
            new DeveloperSpecialist(new OfflineDeveloperSkillAnalysisGenerator()),
            new DeveloperCollaborator(new UnexpectedSynthesisGenerator()),
            new DeveloperReviewerSelector(new OfflineDeveloperReviewerSelector()), catalog,
            new CodeChangeService(db, new OfflineCodeChangeGenerator(), catalog,
                NullLogger<CodeChangeService>.Instance),
            new ConfigurationBuilder().Build(), NullLogger<FeatureWorkflow>.Instance);
        await workflow.GenerateCollaborationSynthesis(id, CancellationToken.None);

        Assert.IsEmpty((await store.GetDeveloperSkillAnalysesAsync(id, CancellationToken.None))!);
        Assert.IsNull(await store.GetCollaborationSynthesisAsync(id, CancellationToken.None));
        Assert.AreEqual(1, await db.WorkflowArtifacts.CountAsync(x =>
            x.RunId == id && x.ArtifactKey == "developer-skills"));
        await store.StorePlanAsync(id, 1, new DraftPlan("Feature", ["Implement it."], []),
            CancellationToken.None, [new DeveloperReviewerMatch("reviewer", ["testing"])]);
        Assert.AreEqual("awaiting_approval", (await store.GetAsync(id, CancellationToken.None)).Status);
        var run = await db.Runs.SingleAsync(x => x.Id == id);
        run.Decision = PlanDecisionKinds.Approve;
        await db.SaveChangesAsync();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            store.RecordOutcomeAsync(id, CancellationToken.None));
        var assignment = await db.ReviewAssignments.SingleAsync(x => x.RunId == id);
        assignment.Decision = DeveloperReviewDecisionKinds.Approve;
        await db.SaveChangesAsync();
        await store.RecordOutcomeAsync(id, CancellationToken.None);
        Assert.AreEqual("code_change_generating", run.Status);
    }

    private sealed class UnexpectedSynthesisGenerator : IDeveloperCollaborationGenerator
    {
        public Task<CollaborationSynthesis> SynthesizeDeveloperCollaborationAsync(
            string input, CancellationToken cancellationToken) =>
            throw new AssertFailedException("An empty specialist selection must not call the synthesis model.");
    }

    private sealed class SubmissionHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
