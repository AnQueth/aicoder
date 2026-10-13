using Collaborator.Agents;
using Collaborator.Core;
using Collaborator.Infrastructure;
using Collaborator.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Collaborator.Tests;

[TestClass]
public sealed class FoundationTests
{
    [TestMethod]
    public void SkillHistoryAndStageArtifactsHaveRevisionBoundKeys()
    {
        using var db = new DesignTimeDbFactory().CreateDbContext([]);
        var model = db.Model;

        var skillKey = model.FindEntityType(typeof(AgentSkillRecord))?.FindPrimaryKey();
        Assert.IsNotNull(skillKey);
        Assert.AreEqual("RoleId,Domain,SkillId",
            string.Join(",", skillKey.Properties.Select(property => property.Name)));

        var skillRevisionKey = model.FindEntityType(typeof(AgentSkillRevisionRecord))?.FindPrimaryKey();
        Assert.IsNotNull(skillRevisionKey);
        Assert.AreEqual("RoleId,Domain,SkillId,Revision",
            string.Join(",", skillRevisionKey.Properties.Select(property => property.Name)));

        var artifactKey = model.FindEntityType(typeof(WorkflowArtifactRecord))?.FindPrimaryKey();
        Assert.IsNotNull(artifactKey);
        Assert.AreEqual("RunId,Stage,ArtifactKey,Revision",
            string.Join(",", artifactKey.Properties.Select(property => property.Name)));
        Assert.AreEqual("jsonb",
            model.FindEntityType(typeof(WorkflowArtifactRecord))!
                .FindProperty(nameof(WorkflowArtifactRecord.SkillRevisionsJson))!.GetColumnType());

        var questionForeignKey = model.FindEntityType(typeof(StageQuestionRecord))?
            .GetForeignKeys()
            .Single(foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(WorkflowArtifactRecord));
        Assert.IsNotNull(questionForeignKey);
        Assert.AreEqual("RunId,Stage,ArtifactKey,Revision",
            string.Join(",", questionForeignKey.Properties.Select(property => property.Name)));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    public void EmptyFeatureIsRejected(string text) =>
        Assert.IsFalse(InputRules.IsText(text));

    [TestMethod]
    public void OversizedFeatureIsRejected() =>
        Assert.IsFalse(InputRules.IsText(new string('a', InputRules.MaximumTextLength + 1)));

    [TestMethod]
    public void InvalidPlanIsRejected() =>
        Assert.ThrowsExactly<InvalidDataException>(() =>
            InputRules.ValidatePlan(new DraftPlan("summary", [], [])));

    [TestMethod]
    public void InvalidPlanReportsTheFieldThatNeedsCorrection()
    {
        var error = Assert.ThrowsExactly<InvalidDataException>(() =>
            InputRules.ValidatePlan(new DraftPlan("summary", [], [])));

        StringAssert.Contains(error.Message, "between 1 and 50 steps");
    }

    [TestMethod]
    public void InvalidFeatureSpecificationReportsTheFieldThatNeedsCorrection()
    {
        var specification = new FeatureSpecification(
            "Invoice export",
            "Users need to export invoices.",
            [],
            ["The export can be downloaded."],
            [],
            [],
            []);

        var error = Assert.ThrowsExactly<InvalidDataException>(() =>
            InputRules.ValidateFeatureSpecification(specification));

        StringAssert.Contains(error.Message, "userStories");
    }

    [TestMethod]
    public void ProductIdCannotContainPathCharacters() =>
        Assert.IsFalse(InputRules.IsProductId("../secrets"));

    [TestMethod]
    public void AgentSkillRevisionRequiresLowercaseIdentifiersAndBoundedContent()
    {
        var valid = new AgentSkillRevisionRequest(
            "Requirements analysis", "Ask clear questions before drafting requirements.");
        Assert.IsTrue(InputRules.IsValidAgentSkillRevision(
            "business-analyst", "general", "requirements-analysis", valid, out _));
        Assert.IsFalse(InputRules.IsValidAgentSkillRevision(
            "Business-analyst", "general", "requirements-analysis", valid, out _));
        Assert.IsFalse(InputRules.IsValidAgentSkillRevision(
            "business-analyst", "general", "requirements-analysis",
            valid with { Prompt = new string('a', InputRules.MaximumAgentSkillPromptLength + 1) }, out _));
        Assert.IsFalse(InputRules.IsValidAgentSkillRevision(
            "business-analyst", "general", "requirements-analysis",
            valid with { ActorId = new string('a', 129) }, out _));
        Assert.IsFalse(InputRules.IsValidAgentSkillRevision(
            "business-analyst", "general", "requirements-analysis",
            valid with { ChangeReason = new string('a', InputRules.MaximumCommentLength + 1) }, out _));
    }

    [TestMethod]
    public void ArchitectureAnalysisRejectsEvidenceOutsideThePinnedSnapshot()
    {
        var analysis = new ArchitectureAnalysis(
            "A bounded architecture assessment.",
            [new ArchitectureComponentImpact(
                "Invoice service",
                "The change affects invoice queries.",
                [new ArchitectureEvidence("src/uninspected.cs", "InvoiceService", 1, 4, "Handles invoice queries.")])],
            [],
            [],
            [],
            []);

        Assert.ThrowsExactly<InvalidDataException>(() =>
            InputRules.ValidateArchitectureAnalysis(
                analysis, new HashSet<string>(["src/invoices.cs"], StringComparer.Ordinal)));
    }

    [TestMethod]
    public void ArchitectureDecisionRequiresRevisionAndReasonExceptForApproval()
    {
        Assert.IsTrue(InputRules.IsValidArchitectureAnalysisDecision(
            new ArchitectureAnalysisDecision(1, ArchitectureAnalysisDecisionKinds.Approve), out _));
        Assert.IsFalse(InputRules.IsValidArchitectureAnalysisDecision(
            new ArchitectureAnalysisDecision(1, ArchitectureAnalysisDecisionKinds.Revise), out _));
        Assert.IsTrue(InputRules.IsValidArchitectureAnalysisDecision(
            new ArchitectureAnalysisDecision(1, ArchitectureAnalysisDecisionKinds.Revise, "Consider the data boundary."),
            out _));
        Assert.IsFalse(InputRules.IsValidArchitectureAnalysisDecision(
            new ArchitectureAnalysisDecision(0, ArchitectureAnalysisDecisionKinds.Approve), out _));
    }

    [TestMethod]
    [DataRow("file:///H:/src/collaborator-sample-app-remote.git", true)]
    [DataRow("https://github.com/example/project.git", true)]
    [DataRow("ssh://github.com/example/project.git", true)]
    [DataRow("https://user:token@example.com/project.git", false)]
    [DataRow("https://example.com/project.git?token=secret", false)]
    [DataRow("relative/path.git", false)]
    [DataRow("ftp://example.com/project.git", false)]
    public void RepositoryUrlValidation(string url, bool expected) =>
        Assert.AreEqual(expected, InputRules.IsRepositoryUrl(url));

    [TestMethod]
    [DataRow("file:///H:/src/sample.git", true)]
    [DataRow("https://github.com/example/project.git", false)]
    [DataRow("ssh://github.com/example/project.git", false)]
    [DataRow("file://server/share/project.git", false)]
    public void RepositoryInspectionRequiresLocalFileUrl(string url, bool expected) =>
        Assert.AreEqual(expected, InputRules.IsLocalFileRepositoryUrl(url));

    [TestMethod]
    public void RepositoryCatalogRejectsLocationsOutsideInspectedFiles()
    {
        var functionality = new RepositoryFunctionality(
            "Task creation", "Creates a task.", ["src/tasks.ts"], []);
        Assert.ThrowsExactly<InvalidDataException>(() =>
            InputRules.ValidateRepositoryCatalog([functionality], new HashSet<string>(StringComparer.Ordinal)
            {
                "src/app.ts"
            }));
    }

    [TestMethod]
    public void RepositoryCatalogAcceptsVerifiedLocations()
    {
        var functionality = new RepositoryFunctionality(
            "Task creation", "Creates a task.", ["src/tasks.ts"], ["TaskService.Create"]);
        InputRules.ValidateRepositoryCatalog([functionality], new HashSet<string>(StringComparer.Ordinal)
        {
            "src/tasks.ts"
        });
    }

    [TestMethod]
    public void RepositorySourceTools_ListSearchAndReadOnlyPinnedEvidence()
    {
        var sourceFiles = new[]
        {
            new RepositoryFileEvidence("src/Tasks.cs", "public sealed class TaskService\n{\n    public void Create() { }\n}\n")
        };
        var tools = new RepositorySourceTools(sourceFiles);

        var paths = JsonSerializer.Deserialize<string[]>(tools.ListSourceFiles());
        var matches = JsonSerializer.Deserialize<JsonElement[]>(tools.SearchSourceCode("taskservice", 5));

        CollectionAssert.AreEqual(new[] { "src/Tasks.cs" }, paths);
        Assert.AreEqual("public sealed class TaskService\n{\n    public void Create() { }\n}\n",
            tools.ReadSourceFile("src/Tasks.cs"));
        Assert.AreEqual("src/Tasks.cs", matches![0].GetProperty("path").GetString());
        Assert.AreEqual(1, matches[0].GetProperty("line").GetInt32());
        Assert.AreEqual(3, tools.CreateTools().Count);
        Assert.ThrowsExactly<KeyNotFoundException>(() => tools.ReadSourceFile("../outside.cs"));
    }

    [TestMethod]
    public void RepositorySourceTools_PrepareInputBindsToolsAndRemovesRawSourceFromPrompt()
    {
        const string marker = "internal-source-marker";
        var input = JsonSerializer.Serialize(new
        {
            requestedFeature = "Add task filtering",
            sourceFiles = new[] { new RepositoryFileEvidence("src/Tasks.cs", marker) }
        }, JsonSerializerOptions.Web);

        var context = RepositorySourceTools.PrepareInput(input);

        Assert.IsFalse(context.Prompt.Contains(marker, StringComparison.Ordinal));
        Assert.AreEqual(3, context.Tools.Count);
        using var prompt = JsonDocument.Parse(context.Prompt);
        Assert.AreEqual("Add task filtering", prompt.RootElement.GetProperty("requestedFeature").GetString());
        Assert.IsFalse(prompt.RootElement.TryGetProperty("sourceFiles", out _));
    }

    [TestMethod]
    [DataRow("../outside.cs")]
    [DataRow("src/appsettings.json")]
    [DataRow("src/client-secret.cs")]
    [DataRow("src/.hidden.cs")]
    [DataRow("src/Tasks.lock")]
    public void RepositorySourceTools_RejectsUnsafeOrExcludedPaths(string path) =>
        Assert.ThrowsExactly<InvalidDataException>(() =>
            new RepositorySourceTools([new RepositoryFileEvidence(path, "text")]));

    [TestMethod]
    public void CodeChangeRequiresSafeUniquePathsAndMatchingOperations()
    {
        var trackedFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["src/Feature.cs"] = "100644",
            ["link.cs"] = "120000"
        };
        var valid = new CodeChangeProposal("Add the feature.", [
            new("src/Feature.cs", CodeFileOperations.Update, "updated"),
            new("tests/FeatureTests.cs", CodeFileOperations.Add, "test"),
            new("src/Old.cs", CodeFileOperations.Delete, null)
        ]);
        trackedFiles["src/Old.cs"] = "100755";
        InputRules.ValidateCodeChange(valid, trackedFiles);

        var invalidPatches = new[]
        {
            new CodeChangeProposal("Unsafe path.", [new("../outside.cs", CodeFileOperations.Add, "x")]),
            new CodeChangeProposal("Secret path.", [new("src/appsettings.json", CodeFileOperations.Add, "x")]),
            new CodeChangeProposal("Existing add.", [new("src/Feature.cs", CodeFileOperations.Add, "x")]),
            new CodeChangeProposal("Symlink update.", [new("link.cs", CodeFileOperations.Update, "x")]),
            new CodeChangeProposal("Duplicate path.", [
                new("src/Feature.cs", CodeFileOperations.Update, "x"),
                new("src/Feature.cs", CodeFileOperations.Update, "y")
            ]),
            new CodeChangeProposal("Missing content.", [new("src/Feature.cs", CodeFileOperations.Delete, "x")])
        };
        foreach (var invalid in invalidPatches)
        {
            Assert.ThrowsExactly<InvalidDataException>(() =>
                InputRules.ValidateCodeChange(invalid, trackedFiles));
        }
    }

    [TestMethod]
    public async Task OfflineAgentProducesExplicitDemonstrationDraft()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Agents:Mode"] = "Offline" }).Build();
        using var services = new ServiceCollection().AddPlanner(config).BuildServiceProvider();
        var plan = await services.GetRequiredService<Planner>()
            .CreateAsync("A test product brief", "Export invoices", CancellationToken.None);
        StringAssert.Contains(plan.Summary, "Offline");
        Assert.IsGreaterThan(0, plan.Steps.Length);
        Assert.IsGreaterThan(0, plan.OpenQuestions.Length);
    }

    [TestMethod]
    public async Task OfflineAgentRevisionIncorporatesFeedback()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Agents:Mode"] = "Offline" }).Build();
        using var services = new ServiceCollection().AddPlanner(config).BuildServiceProvider();
        var planner = services.GetRequiredService<Planner>();
        var first = await planner.CreateAsync("A test product brief", "Export invoices", CancellationToken.None);
        var revised = await planner.CreateAsync(
            "A test product brief", "Export invoices", first, "Add CSV support", 2, CancellationToken.None);
        StringAssert.Contains(revised.Summary, "revision 2");
        Assert.IsTrue(revised.Steps.Any(step => step.Contains("Add CSV support", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task OfflineBusinessAnalystRevisionCarriesForwardFeedback()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Agents:Mode"] = "Offline" }).Build();
        using var services = new ServiceCollection().AddPlanner(config).BuildServiceProvider();
        var analyst = services.GetRequiredService<BusinessAnalyst>();
        var first = await analyst.CreateAsync(
            "Product brief", "Export invoices", "requirements-analysis", "Analyze requirements.",
            null, null, 1, CancellationToken.None);
        var revised = await analyst.CreateAsync(
            "Product brief", "Export invoices", "requirements-analysis", "Analyze requirements.",
            first, "Clarify CSV export acceptance", 2, CancellationToken.None);

        Assert.IsGreaterThan(0, first.AcceptanceCriteria.Length);
        StringAssert.Contains(revised.ProblemStatement, "Clarify CSV export acceptance");
        InputRules.ValidateFeatureSpecification(revised);
    }

    [TestMethod]
    public async Task BusinessAnalystMustApproveSpecificationBeforePlanCanBeStored()
    {
        var options = new DbContextOptionsBuilder<CollaboratorDb>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new CollaboratorDb(options);
        var runId = Guid.NewGuid();
        db.Products.Add(new Product { Id = "sample", Name = "Sample", Brief = "Product brief" });
        db.Runs.Add(new FeatureRun
        {
            Id = runId,
            ProductId = "sample",
            Text = "Export invoices",
            ProductBrief = "Product brief",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            BusinessAnalystId = "analyst",
            BusinessAnalystSkillId = "requirements-analysis",
            BusinessAnalystSkillName = "Requirements analysis",
            BusinessAnalystPrompt = "Analyze requirements.",
            RepositoryCommit = "commit-1",
            ReviewerCount = 1,
            DeveloperSkillsJson = JsonSerializer.Serialize(new[]
            {
                new DeveloperSkillSnapshot(
                    new AgentSkillRevisionReference("developer-ui", "ui-analysis", "general", 2),
                    "UI analysis", "Analyze user-interface changes."),
                new DeveloperSkillSnapshot(
                    new AgentSkillRevisionReference("developer-api", "api-analysis", "general", 1),
                    "API analysis", "Analyze API and backend changes.")
            }, JsonSerializerOptions.Web)
        });
        db.Developers.Add(new DeveloperRecord
        {
            UserId = "architect",
            DisplayName = "Architect",
            RolesJson = "[\"architecture\"]"
        });
        await db.SaveChangesAsync();

        var store = new RunStore(db);
        var plan = new DraftPlan("Export invoices", ["Implement export"], []);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            store.StorePlanAsync(runId, 1, plan, CancellationToken.None));

        var firstSpecification = new FeatureSpecification(
            "Invoice export", "Users need to export invoices.",
            ["As a user, I want to export invoices."],
            ["CSV exports contain the selected invoices."], [], [],
            ["Which invoices should be included?", "Which file format should be used?"]);
        await store.StoreFeatureSpecificationAsync(runId, 1, firstSpecification, CancellationToken.None);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            store.RecordBusinessAnalystDecisionAsync(
                runId,
                new BusinessAnalystDecision(1, BusinessAnalystDecisionKinds.Revise, "Clarify the export"),
                "analyst",
                CancellationToken.None));
        await store.RecordBusinessAnalystDecisionAsync(
            runId,
            new BusinessAnalystDecision(
                1,
                BusinessAnalystDecisionKinds.Revise,
                "Clarify CSV export acceptance",
                Answers:
                [
                    new("which invoices should be included?", "Only invoices selected by the user."),
                    new("Which file format should be used?", "CSV.")
                ]),
            "analyst",
            CancellationToken.None);

        var firstSpecificationRevision = await db.FeatureSpecificationRevisions.SingleAsync(
            x => x.RunId == runId && x.Revision == 1);
        var answers = JsonSerializer.Deserialize<BusinessAnalystQuestionAnswer[]>(
            firstSpecificationRevision.AnswersJson!, JsonSerializerOptions.Web);
        Assert.AreEqual(2, answers?.Length);
        var revisionContext = await store.GetFeatureSpecificationRevisionContextAsync(
            runId, 2, CancellationToken.None);
        Assert.AreEqual(firstSpecification.Title, revisionContext.PreviousSpecification?.Title);
        StringAssert.Contains(revisionContext.Feedback!, "Clarify CSV export acceptance");
        StringAssert.Contains(revisionContext.Feedback!, "Only invoices selected by the user.");
        StringAssert.Contains(revisionContext.Feedback!, "CSV.");

        var revisedSpecification = firstSpecification with
        {
            AcceptanceCriteria = ["CSV exports contain the selected invoices and preserve column headers."]
        };
        await store.StoreFeatureSpecificationAsync(runId, 2, revisedSpecification, CancellationToken.None);
        await store.RecordBusinessAnalystDecisionAsync(
            runId,
            new BusinessAnalystDecision(2, BusinessAnalystDecisionKinds.Approve),
            "analyst",
            CancellationToken.None);
        var architecture = new ArchitectureAnalysis(
            "Invoice export adds a repository-backed export path.",
            [new ArchitectureComponentImpact(
                "Invoice service",
                "The feature needs a way to select and export invoices.",
                [new ArchitectureEvidence("src/invoices.cs", "InvoiceService", 10, 14, "Owns invoice queries.")])],
            [new ArchitectureOption("Service operation", "Add an operation to the existing invoice service.", [], [])],
            [],
            [],
            []);
        await store.StoreArchitectureAnalysisAsync(
            runId,
            1,
            architecture,
            "commit-1",
            new AgentSkillRevisionReference("architect", "architecture-analysis", "general", 1),
            new HashSet<string>(["src/invoices.cs"], StringComparer.Ordinal),
            CancellationToken.None);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            store.StorePlanAsync(runId, 1, plan, CancellationToken.None,
                [new DeveloperReviewerMatch("architect", ["architecture"])]));
        await store.RecordArchitectureAnalysisDecisionAsync(
            runId,
            new ArchitectureAnalysisDecision(
                1, ArchitectureAnalysisDecisionKinds.Revise, "Include the storage boundary."),
            "analyst",
            CancellationToken.None);
        var revisedContext = await store.GetArchitectureRevisionContextAsync(
            runId, 2, CancellationToken.None);
        Assert.AreEqual(architecture.Summary, revisedContext.PreviousAnalysis?.Summary);
        Assert.AreEqual("Include the storage boundary.", revisedContext.Feedback);
        await store.StoreArchitectureAnalysisAsync(
            runId,
            2,
            architecture with { Summary = "Invoice export adds a repository-backed export path and storage boundary." },
            "commit-1",
            new AgentSkillRevisionReference("architect", "architecture-analysis", "general", 1),
            new HashSet<string>(["src/invoices.cs"], StringComparer.Ordinal),
            CancellationToken.None);
        var revisedArtifact = await db.WorkflowArtifacts.SingleAsync(
            x => x.RunId == runId && x.Stage == WorkflowStages.Architecture && x.Revision == 2);
        StringAssert.Contains(revisedArtifact.InputArtifactRevisionsJson!, "previousArchitecture");
        await store.RecordArchitectureAnalysisDecisionAsync(
            runId,
            new ArchitectureAnalysisDecision(2, ArchitectureAnalysisDecisionKinds.Approve),
            "analyst",
            CancellationToken.None);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            store.StorePlanAsync(runId, 1, plan, CancellationToken.None,
                [new DeveloperReviewerMatch("architect", ["architecture"])]));
        var specialistAnalyses = new[]
        {
            new DeveloperSkillAnalysis(
                "developer-ui", "general", "ui-analysis", "UI analysis", 2,
                "Use the existing invoice view.", ["Add an export action."], [],
                ["Verify keyboard access."], []),
            new DeveloperSkillAnalysis(
                "developer-api", "general", "api-analysis", "API analysis", 1,
                "Add a bounded export operation.", ["Add an invoice export endpoint."], [],
                ["Verify selected invoices."], [])
        };
        await store.StoreDeveloperSkillAnalysisAsync(
            runId, specialistAnalyses[0], "commit-1", CancellationToken.None);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            store.CompleteDeveloperSkillAnalysesAsync(runId, CancellationToken.None));
        await store.StoreDeveloperSkillAnalysisAsync(
            runId, specialistAnalyses[1], "commit-1", CancellationToken.None);
        await store.StoreDeveloperSkillAnalysisAsync(
            runId, specialistAnalyses[0], "commit-1", CancellationToken.None);
        await store.CompleteDeveloperSkillAnalysesAsync(runId, CancellationToken.None);
        await store.CompleteDeveloperSkillAnalysesAsync(runId, CancellationToken.None);
        var storedSpecialists = await store.GetDeveloperSkillAnalysesAsync(runId, CancellationToken.None);
        Assert.AreEqual(2, storedSpecialists?.Length);
        var specialistArtifact = await db.WorkflowArtifacts.SingleAsync(
            x => x.RunId == runId && x.Stage == WorkflowStages.SpecialistAnalysis &&
                x.ArtifactKey == "developer-skills");
        Assert.AreEqual(2, JsonSerializer.Deserialize<AgentSkillRevisionReference[]>(
            specialistArtifact.SkillRevisionsJson, JsonSerializerOptions.Web)?.Length);
        Assert.AreEqual(2, await db.WorkflowArtifacts.CountAsync(
            x => x.RunId == runId && x.Stage == WorkflowStages.SpecialistAnalysis &&
                x.ArtifactKey.StartsWith("developer-skill-")));
        var capturedSkillRevisions = specialistAnalyses.Select(analysis =>
            new AgentSkillRevisionReference(
                analysis.RoleId, analysis.SkillId, analysis.Domain, analysis.SkillRevision)).ToArray();
        var synthesis = new CollaborationSynthesis(
            "The roles agree on a user-initiated export but differ on where filtering belongs.",
            [
                new CollaborationFinding(
                    "Export starts from the invoice screen.",
                    [capturedSkillRevisions[0], capturedSkillRevisions[1]])
            ],
            [
                new CollaborationFinding(
                    "UI proposes client-side filtering while API proposes server-side filtering.",
                    [capturedSkillRevisions[0], capturedSkillRevisions[1]])
            ],
            [
                new CollaborationQuestion(
                    "Should export use server-side filtering?",
                    [capturedSkillRevisions[1]])
            ]);
        await store.StoreCollaborationSynthesisAsync(
            runId, synthesis, "commit-1", CancellationToken.None);
        await store.StoreCollaborationSynthesisAsync(
            runId, synthesis, "commit-1", CancellationToken.None);
        Assert.AreEqual(1, await db.WorkflowArtifacts.CountAsync(
            x => x.RunId == runId && x.Stage == WorkflowStages.Synthesis &&
                x.ArtifactKey == "cross-role-synthesis"));
        var storedSynthesis = await store.GetCollaborationSynthesisAsync(runId, CancellationToken.None);
        Assert.AreEqual(1, storedSynthesis?.Conflicts.Length);
        Assert.AreEqual(2, storedSynthesis?.Conflicts[0].Sources.Length);
        await store.StorePlanAsync(runId, 1, plan, CancellationToken.None,
            [new DeveloperReviewerMatch("architect", ["architecture"])]);

        var approvedRun = await store.GetAsync(runId, CancellationToken.None);
        Assert.IsTrue(approvedRun.BusinessAnalystApproved);
        Assert.AreEqual(1, approvedRun.PlanRevision);
        Assert.AreEqual("awaiting_approval", approvedRun.Status);
        var assignment = await db.ReviewAssignments.SingleAsync();
        Assert.AreEqual("architect", assignment.DeveloperId);
        CollectionAssert.AreEqual(new[] { "architecture" },
            JsonSerializer.Deserialize<string[]>(assignment.MatchedRolesJson, JsonSerializerOptions.Web));
        Assert.IsNotNull(approvedRun.SelectedReviewersJson);

        var developerProfile = await db.Developers.SingleAsync(x => x.UserId == "architect");
        developerProfile.RolesJson = "[\"backend\"]";
        await db.SaveChangesAsync();
        await store.StorePlanAsync(runId, 2, plan, CancellationToken.None,
            [new DeveloperReviewerMatch("architect", ["architecture"])]);

        var revisionAssignment = await db.ReviewAssignments.SingleAsync(x => x.Revision == 2);
        CollectionAssert.AreEqual(new[] { "architecture" },
            JsonSerializer.Deserialize<string[]>(revisionAssignment.MatchedRolesJson, JsonSerializerOptions.Web));
    }

    [TestMethod]
    public async Task ImplementationOutcomeRequiresUnanimousReviewOfCurrentPlan()
    {
        var options = new DbContextOptionsBuilder<CollaboratorDb>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new CollaboratorDb(options);
        var runId = Guid.NewGuid();
        db.Products.Add(new Product { Id = "sample", Name = "Sample", Brief = "Product brief" });
        db.Runs.Add(new FeatureRun
        {
            Id = runId,
            ProductId = "sample",
            Text = "Add search",
            ProductBrief = "Product brief",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            BusinessAnalystId = "analyst",
            BusinessAnalystSkillId = "requirements-analysis",
            BusinessAnalystSkillName = "Requirements analysis",
            BusinessAnalystPrompt = "Analyze requirements.",
            BusinessAnalystApproved = true,
            ReviewerCount = 2,
            PlanRevision = 1,
            Decision = PlanDecisionKinds.Approve,
            Status = "awaiting_approval",
            DeveloperSkillsJson = JsonSerializer.Serialize(new[]
            {
                new DeveloperSkillSnapshot(
                    new AgentSkillRevisionReference("developer-general", "implementation-analysis", "general", 1),
                    "General developer analysis", "Analyze the change.")
            }, JsonSerializerOptions.Web)
        });
        db.PlanRevisions.Add(new PlanRevisionRecord
        {
            RunId = runId,
            Revision = 1,
            PlanJson = JsonSerializer.Serialize(new DraftPlan("Search", ["Implement search"], []))
        });
        db.WorkflowArtifacts.Add(new WorkflowArtifactRecord
        {
            RunId = runId,
            Stage = WorkflowStages.Architecture,
            ArtifactKey = "architecture-analysis",
            Revision = 1,
            ArtifactType = nameof(ArchitectureAnalysis),
            PayloadJson = JsonSerializer.Serialize(new ArchitectureAnalysis(
                "Approved architecture.", [], [], [], [], []), JsonSerializerOptions.Web),
            RepositoryCommit = "commit-1",
            Status = WorkflowArtifactStatuses.Approved,
            Decision = ArchitectureAnalysisDecisionKinds.Approve
        });
        db.WorkflowArtifacts.Add(new WorkflowArtifactRecord
        {
            RunId = runId,
            Stage = WorkflowStages.SpecialistAnalysis,
            ArtifactKey = "developer-skills",
            Revision = 1,
            ArtifactType = nameof(DeveloperSkillAnalysis),
            PayloadJson = JsonSerializer.Serialize(new[]
            {
                new DeveloperSkillAnalysis(
                    "developer-general", "general", "implementation-analysis",
                    "General developer analysis", 1, "Review the implementation.",
                    ["Implement search."], [], ["Run unit tests."], [])
            }, JsonSerializerOptions.Web),
            SkillRevisionsJson = JsonSerializer.Serialize(new[]
            {
                new AgentSkillRevisionReference(
                    "developer-general", "implementation-analysis", "general", 1)
            }, JsonSerializerOptions.Web),
            Status = WorkflowArtifactStatuses.Completed
        });
        await db.SaveChangesAsync();
        var store = new RunStore(db);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            store.RecordOutcomeAsync(runId, CancellationToken.None));

        db.Developers.AddRange(
            new DeveloperRecord { UserId = "alice", DisplayName = "Alice" },
            new DeveloperRecord { UserId = "bob", DisplayName = "Bob" });
        db.ReviewAssignments.AddRange(
            new ReviewAssignmentRecord
            {
                RunId = runId, Revision = 1, DeveloperId = "alice", DeveloperDisplayName = "Alice",
                ReviewPrompt = "Review", Decision = DeveloperReviewDecisionKinds.Approve,
                RespondedAt = DateTimeOffset.UtcNow
            },
            new ReviewAssignmentRecord
            {
                RunId = runId, Revision = 1, DeveloperId = "bob", DeveloperDisplayName = "Bob",
                ReviewPrompt = "Review", Decision = DeveloperReviewDecisionKinds.Revise,
                Response = "Cover empty search results.", RespondedAt = DateTimeOffset.UtcNow
            });
        await db.SaveChangesAsync();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            store.RecordOutcomeAsync(runId, CancellationToken.None));
        var bobReview = await db.ReviewAssignments.SingleAsync(x => x.DeveloperId == "bob");
        bobReview.Decision = DeveloperReviewDecisionKinds.Approve;
        bobReview.Response = null;
        await db.SaveChangesAsync();

        var run = await store.GetAsync(runId, CancellationToken.None);
        await store.RecordOutcomeAsync(runId, CancellationToken.None);
        Assert.AreEqual("code_change_generating", run.Status);
    }

    [TestMethod]
    [DataRow("approve", null, true)]
    [DataRow("approve", "looks good", true)]
    [DataRow("reject", "out of scope", true)]
    [DataRow("revise", "add CSV support", true)]
    [DataRow("reject", null, false)]
    [DataRow("revise", " ", false)]
    [DataRow("maybe", "x", false)]
    public void DecisionValidation(string kind, string? comment, bool expected) =>
        Assert.AreEqual(expected, InputRules.IsValidDecision(new PlanDecision(1, kind, comment), out _));

    [TestMethod]
    public void DecisionRequiresPositiveRevisionAndBoundedComment()
    {
        Assert.IsFalse(InputRules.IsValidDecision(new PlanDecision(0, PlanDecisionKinds.Approve), out _));
        Assert.IsFalse(InputRules.IsValidDecision(null, out _));
        Assert.IsFalse(InputRules.IsValidDecision(new PlanDecision(1, PlanDecisionKinds.Approve,
            new string('a', InputRules.MaximumCommentLength + 1)), out _));
    }

    [TestMethod]
    public void DeveloperConfigurationRequiresIdAndDisplayName()
    {
        Assert.IsTrue(InputRules.IsValidDeveloper("alice",
            new DeveloperConfiguration("Alice", Roles: ["architecture"]), out _));
        Assert.IsFalse(InputRules.IsValidDeveloper(" ",
            new DeveloperConfiguration("Alice", Roles: ["architecture"]), out _));
        Assert.IsFalse(InputRules.IsValidDeveloper("alice",
            new DeveloperConfiguration(" ", Roles: ["architecture"]), out _));
        Assert.IsFalse(InputRules.IsValidDeveloper("alice", new DeveloperConfiguration(
            "Alice", new string('x', 1_001), Roles: ["architecture"]), out _));
        Assert.IsFalse(InputRules.IsValidDeveloper("alice",
            new DeveloperConfiguration("Alice"), out _));
    }

    [TestMethod]
    public void ReviewAssignmentResponseMustBeNonemptyAndBounded()
    {
        Assert.IsTrue(InputRules.IsValidReviewAssignmentResponse(
            new ReviewAssignmentResponse(DeveloperReviewDecisionKinds.Approve, "Looks good, add a test."), out _));
        Assert.IsTrue(InputRules.IsValidReviewAssignmentResponse(
            new ReviewAssignmentResponse(DeveloperReviewDecisionKinds.Approve), out _));
        Assert.IsFalse(InputRules.IsValidReviewAssignmentResponse(
            new ReviewAssignmentResponse("maybe"), out _));
        Assert.IsFalse(InputRules.IsValidReviewAssignmentResponse(new ReviewAssignmentResponse(
            DeveloperReviewDecisionKinds.Revise), out _));
        Assert.IsFalse(InputRules.IsValidReviewAssignmentResponse(new ReviewAssignmentResponse(
            DeveloperReviewDecisionKinds.Approve, new string('x', InputRules.MaximumCommentLength + 1)), out _));
        Assert.IsFalse(InputRules.IsValidReviewAssignmentResponse(null, out _));
    }

    [TestMethod]
    public async Task OfflineReviewerSelectorChoosesRequestedDistinctProfilesAndConfiguredRoles()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Agents:Mode"] = "Offline" }).Build();
        using var services = new ServiceCollection().AddPlanner(config).BuildServiceProvider();
        var selection = await services.GetRequiredService<DeveloperReviewerSelector>().SelectAsync(
            "Build an architecture for the database API.",
            new FeatureSpecification("Data API", "Provide a data API.", ["As a user, I need data."],
                ["API returns data."], [], [], []),
            [
                new DeveloperConfigurationProfile("alice", "Alice", ["architecture"], null),
                new DeveloperConfigurationProfile("bob", "Bob", ["database"], null),
                new DeveloperConfigurationProfile("carol", "Carol", ["frontend"], null)
            ],
            2,
            CancellationToken.None);

        Assert.AreEqual(2, selection.Length);
        Assert.AreEqual(2, selection.Select(x => x.UserId).Distinct(StringComparer.Ordinal).Count());
        Assert.IsTrue(selection.Any(x => x.UserId == "alice" && x.MatchedRoles.Contains("architecture")));
        Assert.IsTrue(selection.Any(x => x.UserId == "bob" && x.MatchedRoles.Contains("database")));
    }

    [TestMethod]
    public void ReviewerCountIsRequiredToBeWithinSupportedBounds()
    {
        Assert.IsTrue(InputRules.IsValidFeatureSubmission(
            new FeatureSubmission("Add search", ReviewerCount: 3), out _));
        Assert.IsFalse(InputRules.IsValidFeatureSubmission(
            new FeatureSubmission("Add search", ReviewerCount: 0), out _));
        Assert.IsFalse(InputRules.IsValidFeatureSubmission(
            new FeatureSubmission("Add search", ReviewerCount: InputRules.MaximumReviewerCount + 1), out _));
    }

    [TestMethod]
    public async Task RevisionContextIncludesCoordinatorAndCompletedDeveloperFeedbackOnly()
    {
        var options = new DbContextOptionsBuilder<CollaboratorDb>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new CollaboratorDb(options);
        var runId = Guid.NewGuid();
        db.Products.Add(new Product { Id = "sample", Name = "Sample", Brief = "Sample brief" });
        db.Runs.Add(new FeatureRun
        {
            Id = runId,
            ProductId = "sample",
            Text = "Export data",
            ProductBrief = "Sample brief",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            BusinessAnalystId = "default",
            BusinessAnalystSkillId = "requirements-analysis",
            BusinessAnalystSkillName = "Requirements analysis",
            BusinessAnalystPrompt = "Analyze the request."
        });
        db.Developers.AddRange(
            new DeveloperRecord { UserId = "alice", DisplayName = "Alice" },
            new DeveloperRecord { UserId = "bob", DisplayName = "Bob" },
            new DeveloperRecord { UserId = "carol", DisplayName = "Carol" });
        db.PlanRevisions.Add(new PlanRevisionRecord
        {
            RunId = runId,
            Revision = 1,
            PlanJson = JsonSerializer.Serialize(new DraftPlan("Initial plan", ["Implement export"], [])),
            Decision = PlanDecisionKinds.Revise,
            Comment = "Please include CSV export."
        });
        db.ReviewAssignments.AddRange(
            new ReviewAssignmentRecord
            {
                RunId = runId,
                Revision = 1,
                DeveloperId = "alice",
                DeveloperDisplayName = "Alice",
                ReviewPrompt = "Review the plan.",
                Response = "Add authorization coverage.",
                RespondedAt = DateTimeOffset.UtcNow
            },
            new ReviewAssignmentRecord
            {
                RunId = runId,
                Revision = 1,
                DeveloperId = "bob",
                DeveloperDisplayName = "Bob",
                ReviewPrompt = "Review the plan."
            },
            new ReviewAssignmentRecord
            {
                RunId = runId,
                Revision = 2,
                DeveloperId = "carol",
                DeveloperDisplayName = "Carol",
                ReviewPrompt = "Review the next plan.",
                Response = "Not from the previous revision.",
                RespondedAt = DateTimeOffset.UtcNow
            });
        await db.SaveChangesAsync();

        var context = await new RunStore(db).GetRevisionContextAsync(runId, 2, CancellationToken.None);

        Assert.AreEqual("Initial plan", context.PreviousPlan?.Summary);
        Assert.IsNotNull(context.Feedback);
        StringAssert.Contains(context.Feedback, "Coordinator feedback:");
        StringAssert.Contains(context.Feedback, "Please include CSV export.");
        StringAssert.Contains(context.Feedback, "Reviewer Alice (alice):");
        StringAssert.Contains(context.Feedback, "Add authorization coverage.");
        Assert.IsFalse(context.Feedback.Contains("Bob", StringComparison.Ordinal));
        Assert.IsFalse(context.Feedback.Contains("Carol", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ApprovedCodeChangePublishesOnlyAReviewBranchFromPinnedCommit()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"collaborator-test-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(temporaryRoot, "source");
        var remotePath = Path.Combine(temporaryRoot, "remote.git");
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            Directory.CreateDirectory(sourcePath);
            await RunTestGitAsync(sourcePath, "init", "-b", "main");
            await RunTestGitAsync(sourcePath, "config", "user.name", "Collaborator test");
            await RunTestGitAsync(sourcePath, "config", "user.email", "test@localhost");
            await File.WriteAllTextAsync(Path.Combine(sourcePath, "README.md"), "original\n");
            await RunTestGitAsync(sourcePath, "add", "--", "README.md");
            await RunTestGitAsync(sourcePath, "commit", "-m", "Initial");
            var pinnedCommit = (await RunTestGitAsync(sourcePath, "rev-parse", "HEAD")).Trim();
            await RunTestGitAsync(temporaryRoot, "clone", "--bare", sourcePath, remotePath);
            var repositoryUrl = new Uri(remotePath).AbsoluteUri;

            var options = new DbContextOptionsBuilder<CollaboratorDb>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            await using var db = new CollaboratorDb(options);
            var runId = Guid.NewGuid();
            var plan = new DraftPlan("Add a feature.", ["Update the readme."], []);
            var catalog = new RepositoryCatalog(pinnedCommit, DateTimeOffset.UtcNow, 1, [
                new RepositoryFunctionality("Documentation", "Documents the app.", ["README.md"], [])
            ]);
            db.Products.Add(new Product { Id = "sample", Name = "Sample", Brief = "A sample app." });
            db.Runs.Add(new FeatureRun
            {
                Id = runId,
                ProductId = "sample",
                Text = "Add feature",
                ProductBrief = "A sample app.",
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                BusinessAnalystId = "default",
                BusinessAnalystSkillId = "requirements-analysis",
                BusinessAnalystSkillName = "Requirements analysis",
                BusinessAnalystPrompt = "Analyze the request.",
                FeatureSpecificationRevision = 1,
                BusinessAnalystApproved = true,
                RepositoryUrl = repositoryUrl,
                RepositoryCommit = pinnedCommit,
                RepositoryCatalogJson = JsonSerializer.Serialize(catalog, JsonSerializerOptions.Web),
                PlanJson = JsonSerializer.Serialize(plan, JsonSerializerOptions.Web),
                PlanRevision = 1,
                Decision = PlanDecisionKinds.Approve,
                Status = "code_change_generating",
                DeveloperSkillsJson = JsonSerializer.Serialize(new[]
                {
                    new DeveloperSkillSnapshot(
                        new AgentSkillRevisionReference(
                            "developer-general", "implementation-analysis", "general", 1),
                        "General developer analysis", "Analyze the change.")
                }, JsonSerializerOptions.Web)
            });
            db.PlanRevisions.Add(new PlanRevisionRecord
            {
                RunId = runId,
                Revision = 1,
                PlanJson = JsonSerializer.Serialize(plan, JsonSerializerOptions.Web),
                Decision = PlanDecisionKinds.Approve
            });
            db.FeatureSpecificationRevisions.Add(new FeatureSpecificationRevisionRecord
            {
                RunId = runId,
                Revision = 1,
                SpecificationJson = JsonSerializer.Serialize(new FeatureSpecification(
                    "Feature", "Problem", ["As a user, I want this."], ["It works."], [], [], []),
                    JsonSerializerOptions.Web),
                Decision = BusinessAnalystDecisionKinds.Approve
            });
            db.WorkflowArtifacts.Add(new WorkflowArtifactRecord
            {
                RunId = runId,
                Stage = WorkflowStages.Architecture,
                ArtifactKey = "architecture-analysis",
                Revision = 1,
                ArtifactType = nameof(ArchitectureAnalysis),
                PayloadJson = JsonSerializer.Serialize(new ArchitectureAnalysis(
                    "Approved architecture.", [], [], [], [], []), JsonSerializerOptions.Web),
                RepositoryCommit = pinnedCommit,
                Status = WorkflowArtifactStatuses.Approved,
                Decision = ArchitectureAnalysisDecisionKinds.Approve
            });
            db.WorkflowArtifacts.Add(new WorkflowArtifactRecord
            {
                RunId = runId,
                Stage = WorkflowStages.SpecialistAnalysis,
                ArtifactKey = "developer-skills",
                Revision = 1,
                ArtifactType = nameof(DeveloperSkillAnalysis),
                PayloadJson = JsonSerializer.Serialize(new[]
                {
                    new DeveloperSkillAnalysis(
                        "developer-general", "general", "implementation-analysis",
                        "General developer analysis", 1, "Review the implementation.",
                        ["Implement documentation."], [], ["Run tests."], [])
                }, JsonSerializerOptions.Web),
                Status = WorkflowArtifactStatuses.Completed
            });
            await db.SaveChangesAsync();

            var proposal = new CodeChangeProposal("Document the feature.", [
                new("README.md", CodeFileOperations.Update, "Updated by the generated patch.\n"),
                new("docs/feature.md", CodeFileOperations.Add, "Review this feature.\n")
            ]);
            var generator = new RecordingCodeChangeGenerator(proposal);
            var catalogService = new RepositoryCatalogService(
                db, new UnexpectedCatalogGenerator(), NullLogger<RepositoryCatalogService>.Instance);
            var service = new CodeChangeService(
                db, generator, catalogService, NullLogger<CodeChangeService>.Instance);

            await service.GenerateAsync(runId, CancellationToken.None);
            await service.PrepareBranchAsync(runId, CancellationToken.None);
            await service.PublishBranchAsync(runId, CancellationToken.None);

            var change = await db.CodeChanges.SingleAsync(x => x.RunId == runId);
            var updatedRun = await db.Runs.SingleAsync(x => x.Id == runId);
            var refName = $"refs/heads/collaborator/feature-{runId:N}";
            var remoteCommit = (await RunTestGitAsync(temporaryRoot,
                "--git-dir", remotePath, "rev-parse", refName)).Trim();
            var publishedContent = await RunTestGitAsync(temporaryRoot,
                "--git-dir", remotePath, "show", $"{refName}:README.md");
            var publishedAddedContent = await RunTestGitAsync(temporaryRoot,
                "--git-dir", remotePath, "show", $"{refName}:docs/feature.md");
            var baseAddedPath = await RunTestGitAsync(temporaryRoot,
                "--git-dir", remotePath, "ls-tree", "refs/heads/main", "--", "docs/feature.md");
            var baseBranch = (await RunTestGitAsync(temporaryRoot,
                "--git-dir", remotePath, "rev-parse", "refs/heads/main")).Trim();

            Assert.AreEqual(CodeChangeStatuses.Ready, change.Status);
            Assert.AreEqual("code_change_ready", updatedRun.Status);
            Assert.AreEqual(pinnedCommit, baseBranch);
            Assert.AreEqual(change.BranchCommit, remoteCommit);
            Assert.AreEqual("Updated by the generated patch.\n", publishedContent);
            Assert.AreEqual("Review this feature.\n", publishedAddedContent);
            Assert.AreEqual(string.Empty, baseAddedPath);
            Assert.AreEqual($"collaborator/feature-{runId:N}", change.BranchName);
            Assert.AreEqual(1, generator.Calls);

            change.Status = CodeChangeStatuses.BranchPrepared;
            updatedRun.Status = "code_change_generating";
            await db.SaveChangesAsync();
            await service.PublishBranchAsync(runId, CancellationToken.None);
            Assert.AreEqual(CodeChangeStatuses.Ready, change.Status);
            Assert.AreEqual("code_change_ready", updatedRun.Status);
            Assert.AreEqual(remoteCommit, (await RunTestGitAsync(temporaryRoot,
                "--git-dir", remotePath, "rev-parse", refName)).Trim());
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                DeleteTestDirectory(temporaryRoot);
            }
        }
    }

    [TestMethod]
    public async Task RejectedPlanCannotInvokeCodeChangeGenerator()
    {
        var options = new DbContextOptionsBuilder<CollaboratorDb>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new CollaboratorDb(options);
        var runId = Guid.NewGuid();
        db.Products.Add(new Product { Id = "sample", Name = "Sample", Brief = "Sample brief" });
        db.Runs.Add(new FeatureRun
        {
            Id = runId,
            ProductId = "sample",
            Text = "Feature",
            ProductBrief = "Sample brief",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            BusinessAnalystId = "default",
            BusinessAnalystSkillId = "requirements-analysis",
            BusinessAnalystSkillName = "Requirements analysis",
            BusinessAnalystPrompt = "Analyze the request.",
            BusinessAnalystApproved = true,
            Decision = PlanDecisionKinds.Reject,
            Status = "plan_rejected"
        });
        await db.SaveChangesAsync();

        var generator = new RecordingCodeChangeGenerator(
            new CodeChangeProposal("Should not be used.", [new("README.md", CodeFileOperations.Add, "x")]));
        var service = new CodeChangeService(
            db,
            generator,
            new RepositoryCatalogService(db, new UnexpectedCatalogGenerator(),
                NullLogger<RepositoryCatalogService>.Instance),
            NullLogger<CodeChangeService>.Instance);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            service.GenerateAsync(runId, CancellationToken.None));
        Assert.AreEqual(0, generator.Calls);
    }

    [TestMethod]
    public async Task CodeChangeCannotStartUntilBusinessAnalystApproves()
    {
        var options = new DbContextOptionsBuilder<CollaboratorDb>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new CollaboratorDb(options);
        var runId = Guid.NewGuid();
        db.Products.Add(new Product { Id = "sample", Name = "Sample", Brief = "Sample brief" });
        db.Runs.Add(new FeatureRun
        {
            Id = runId,
            ProductId = "sample",
            Text = "Feature",
            ProductBrief = "Sample brief",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            BusinessAnalystId = "default",
            BusinessAnalystSkillId = "requirements-analysis",
            BusinessAnalystSkillName = "Requirements analysis",
            BusinessAnalystPrompt = "Analyze the request.",
            Decision = PlanDecisionKinds.Approve,
            Status = "code_change_generating"
        });
        await db.SaveChangesAsync();

        var generator = new RecordingCodeChangeGenerator(
            new CodeChangeProposal("Should not be used.", [new("README.md", CodeFileOperations.Add, "x")]));
        var service = new CodeChangeService(
            db,
            generator,
            new RepositoryCatalogService(db, new UnexpectedCatalogGenerator(),
                NullLogger<RepositoryCatalogService>.Instance),
            NullLogger<CodeChangeService>.Instance);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            service.GenerateAsync(runId, CancellationToken.None));
        Assert.AreEqual(0, generator.Calls);
    }

    [TestMethod]
    public void MissingModeCannotSilentlyUseOffline()
    {
        var config = new ConfigurationBuilder().Build();
        Assert.ThrowsExactly<InvalidOperationException>(() => new ServiceCollection().AddPlanner(config));
    }

    [TestMethod]
    [DataRow("AzureOpenAI")]
    [DataRow("unknown")]
    public void UnsupportedModeIsRejected(string mode)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Agents:Mode"] = mode }).Build();
        Assert.ThrowsExactly<InvalidOperationException>(() => new ServiceCollection().AddPlanner(config));
    }

    [TestMethod]
    public void CopilotModeRegistersSdkProvider()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Agents:Mode"] = "Copilot" }).Build();
        using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IConfiguration>(config)
            .AddPlanner(config)
            .BuildServiceProvider();
        Assert.IsInstanceOfType<CopilotPlanGenerator>(services.GetRequiredService<IPlanGenerator>());
        Assert.IsInstanceOfType<CopilotPlanGenerator>(
            services.GetRequiredService<IRepositoryCatalogGenerator>());
        Assert.IsNotNull(services.GetRequiredService<Planner>());
    }

    [TestMethod]
    public async Task PlannerPassesRevisionContextAsJsonData()
    {
        var generator = new RecordingGenerator(new DraftPlan("Revised", ["Add CSV"], []));
        var previous = new DraftPlan("Initial", ["Export"], ["Which format?"]);
        var result = await new Planner(generator).CreateAsync(
            "Brief", "Export", previous, "Add CSV \"support\"", 2, CancellationToken.None);
        using var document = JsonDocument.Parse(generator.Input!);
        var input = document.RootElement;
        Assert.AreEqual("Brief", input.GetProperty("productBrief").GetString());
        Assert.AreEqual("Export", input.GetProperty("requestedFeature").GetString());
        Assert.AreEqual(2, input.GetProperty("revision").GetInt32());
        Assert.AreEqual("Initial", input.GetProperty("previousPlan").GetProperty("summary").GetString());
        Assert.AreEqual("Add CSV \"support\"", input.GetProperty("reviewerFeedback").GetString());
        Assert.AreEqual("Revised", result.Summary);
    }

    [TestMethod]
    public async Task PlannerIncludesPinnedRepositoryCatalogAsEvidence()
    {
        var generator = new RecordingGenerator(new DraftPlan(
            "Add task filtering", ["Reuse TaskService in src/tasks.ts."], []));
        var sourceFiles = new[] { new RepositoryFileEvidence("src/tasks.ts", "class TaskService {}") };
        var catalog = new RepositoryCatalog(
            "da420a5846bbca95e4be5b7d9eb7a6f00387536d",
            DateTimeOffset.UnixEpoch,
            1,
            [new RepositoryFunctionality("Task service", "Creates tasks.", ["src/tasks.ts"], ["TaskService"])]);
        await new Planner(generator).CreateAsync(
            "Brief", "Filter tasks", null, null, null, 1, catalog, sourceFiles, CancellationToken.None);
        using var document = JsonDocument.Parse(generator.Input!);
        var evidence = document.RootElement.GetProperty("repositoryCatalog");
        Assert.AreEqual(catalog.Commit, evidence.GetProperty("commit").GetString());
        Assert.AreEqual("src/tasks.ts",
            evidence.GetProperty("functionalities")[0].GetProperty("paths")[0].GetString());
        Assert.AreEqual("src/tasks.ts",
            document.RootElement.GetProperty("sourceFiles")[0].GetProperty("path").GetString());
    }

    [TestMethod]
    public async Task PlannerReceivesEveryCapturedDeveloperSkillAnalysis()
    {
        var generator = new RecordingGenerator(new DraftPlan("Export invoices", ["Implement export."], []));
        var analyses = new[]
        {
            new DeveloperSkillAnalysis(
                "developer-ui", "general", "ui-analysis", "UI analysis", 2,
                "Add an export action.", ["Add a button."], [], ["Check keyboard access."], []),
            new DeveloperSkillAnalysis(
                "developer-backend", "general", "api-analysis", "Backend analysis", 1,
                "Add a bounded endpoint.", ["Add an export endpoint."], [], ["Verify authorization."], [])
        };

        await new Planner(generator).CreateAsync(
            "Brief", "Export invoices", null, null, null, 1,
            null, [], CancellationToken.None, analyses);

        using var input = JsonDocument.Parse(generator.Input!);
        var serializedAnalyses = input.RootElement.GetProperty("specialistAnalyses");
        Assert.AreEqual(2, serializedAnalyses.GetArrayLength());
        Assert.AreEqual("developer-ui", serializedAnalyses[0].GetProperty("roleId").GetString());
        Assert.AreEqual("developer-backend", serializedAnalyses[1].GetProperty("roleId").GetString());
    }

    [TestMethod]
    public async Task PlannerReceivesCollaborationSynthesisAlongsideAllOriginalRoleAnalyses()
    {
        var generator = new RecordingGenerator(new DraftPlan("Export invoices", ["Implement export."], []));
        var skills = new[]
        {
            new AgentSkillRevisionReference("developer-ui", "ui-analysis", "general", 2),
            new AgentSkillRevisionReference("developer-backend", "api-analysis", "general", 1)
        };
        var analyses = new[]
        {
            new DeveloperSkillAnalysis(
                skills[0].RoleId, skills[0].Domain, skills[0].SkillId, "UI", skills[0].Revision,
                "Add an export action.", ["Add a button."], [], [], []),
            new DeveloperSkillAnalysis(
                skills[1].RoleId, skills[1].Domain, skills[1].SkillId, "Backend", skills[1].Revision,
                "Add an endpoint.", ["Add an export endpoint."], [], [], [])
        };
        var synthesis = new CollaborationSynthesis(
            "The roles agree on a user-initiated export but disagree on filtering.",
            [new CollaborationFinding("Export is user initiated.", skills)],
            [new CollaborationFinding("Filtering should be client-side or server-side.", skills)],
            []);

        await new Planner(generator).CreateAsync(
            "Brief", "Export invoices", null, null, null, 1,
            null, [], CancellationToken.None, analyses, synthesis);

        using var input = JsonDocument.Parse(generator.Input!);
        Assert.AreEqual(2, input.RootElement.GetProperty("specialistAnalyses").GetArrayLength());
        var serializedSynthesis = input.RootElement.GetProperty("collaborationSynthesis");
        Assert.AreEqual(1, serializedSynthesis.GetProperty("conflicts").GetArrayLength());
        Assert.AreEqual(2,
            serializedSynthesis.GetProperty("conflicts")[0].GetProperty("sources").GetArrayLength());
    }

    [TestMethod]
    public async Task DeveloperCollaborator_ComparesEveryCapturedSkillAndValidatesSourceRevisions()
    {
        var skills = new[]
        {
            new AgentSkillRevisionReference("developer-ui", "ui-analysis", "general", 2),
            new AgentSkillRevisionReference("developer-backend", "api-analysis", "general", 1)
        };
        var analyses = new[]
        {
            new DeveloperSkillAnalysis(
                skills[0].RoleId, skills[0].Domain, skills[0].SkillId, "UI", skills[0].Revision,
                "Add an export action.", ["Add a button."], [], [], []),
            new DeveloperSkillAnalysis(
                skills[1].RoleId, skills[1].Domain, skills[1].SkillId, "Backend", skills[1].Revision,
                "Add an endpoint.", ["Add an export endpoint."], [], [], [])
        };
        var generator = new RecordingCollaborationGenerator(new CollaborationSynthesis(
            "The roles disagree on filtering.",
            [],
            [new CollaborationFinding("Use client-side or server-side filtering.", skills)],
            []));

        var result = await new DeveloperCollaborator(generator).SynthesizeAsync(
            analyses, skills, CancellationToken.None);

        Assert.AreEqual(1, result.Conflicts.Length);
        using var input = JsonDocument.Parse(generator.Input!);
        Assert.AreEqual(2, input.RootElement.GetProperty("specialistAnalyses").GetArrayLength());
        Assert.AreEqual(2, input.RootElement.GetProperty("capturedSkills").GetArrayLength());

        var invalidGenerator = new RecordingCollaborationGenerator(result with
        {
            Conflicts =
            [
                new CollaborationFinding("Unattributed conflict.", [
                    new AgentSkillRevisionReference("developer-unconfigured", "new", "general", 1),
                    skills[1]
                ])
            ]
        });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            new DeveloperCollaborator(invalidGenerator).SynthesizeAsync(
                analyses, skills, CancellationToken.None));
    }

    [TestMethod]
    public async Task BusinessAnalystReceivesBoundedRepositoryEvidence()
    {
        var generator = new RecordingFeatureSpecificationGenerator(new FeatureSpecification(
            "Title", "Problem", ["As a user, I want this."], ["It works."], [], [], []));
        var sourceFiles = new[] { new RepositoryFileEvidence("src/Tasks.cs", "class TaskService {}") };

        await new BusinessAnalyst(generator).CreateAsync(
            "Brief", "Add filtering", "requirements-analysis", "Analyze requirements.",
            null, null, 1, CancellationToken.None, sourceFiles);

        using var input = JsonDocument.Parse(generator.Input!);
        Assert.AreEqual("src/Tasks.cs",
            input.RootElement.GetProperty("sourceFiles")[0].GetProperty("path").GetString());
    }

    [TestMethod]
    [DataRow("", 1)]
    [DataRow("   ", 1)]
    [DataRow("Invalid", 0)]
    [DataRow("Invalid", 51)]
    public async Task PlannerRejectsInvalidProviderOutput(string summary, int stepCount)
    {
        var planner = new Planner(new RecordingGenerator(
            new DraftPlan(summary, Enumerable.Repeat("Step", stepCount).ToArray(), [])));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            planner.CreateAsync("Brief", "Export", CancellationToken.None));
    }

    [TestMethod]
    public async Task OfflinePlanningHonorsCancellation()
    {
        var planner = new Planner(new OfflinePlanGenerator());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            planner.CreateAsync("Brief", "Export", cancellation.Token));
    }

    [TestMethod]
    [TestCategory("LiveCopilot")]
    public async Task CopilotUsesRepositoryToolsToGroundAndRevisePlan()
    {/*
        if (Environment.GetEnvironmentVariable("COLLABORATOR_TEST_COPILOT") != "1")
        {
            Assert.Inconclusive("Set COLLABORATOR_TEST_COPILOT=1 to run this test using Copilot access and usage.");
        }
        */
        const string sourceOnlyMethodName = "RenderLegacyInvoiceArchive_7QK4";
        var sourceFiles = new[]
        {
            new RepositoryFileEvidence(
                "src/InvoiceExportService.cs",
                """
                internal sealed class InvoiceExportService
                {
                    public byte[] RenderLegacyInvoiceArchive_7QK4(Invoice invoice)
                    {
                        return SerializeAsLegacyArchive(invoice);
                    }
                }
                """)
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Agents:Mode"] = "Copilot" }).Build();
        using var loggerProvider = new CapturingLoggerProvider();
        using var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(config)
            .AddLogging(logging => logging.AddProvider(loggerProvider))
            .AddPlanner(config).BuildServiceProvider();
        var planner = services.GetRequiredService<Planner>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(7));
        var first = await planner.CreateAsync(
            "A fictional invoice application.",
            "Add CSV export for invoices. Before planning, list source files, search for the existing invoice export implementation, and read its matching file. Identify the exact existing export method name and path in a plan step; do not guess.",
            null, null, null, 1, null, sourceFiles, deadline.Token);
        InputRules.ValidatePlan(first);
        var firstPlanJson = JsonSerializer.Serialize(first);
        Assert.Contains(sourceOnlyMethodName, firstPlanJson,
            "The plan must identify a symbol available only from the repository tool response.");
        var toolLogs = loggerProvider.Messages
            .Where(message => message.Contains("Copilot repository source tool invoked:", StringComparison.Ordinal))
            .ToArray();
        foreach (var toolName in new[]
        {
            "list_repository_source_files",
            "search_repository_source",
            "read_repository_source_file"
        })
        {
            Assert.IsTrue(toolLogs.Any(message => message.Contains(toolName, StringComparison.Ordinal)),
                $"Expected Copilot to invoke repository tool '{toolName}'.");
        }

        var revised = await planner.CreateAsync(
            "A fictional invoice application.",
            "Add CSV export for invoices.", null, first, "Include CSV export.", 2, null, sourceFiles, deadline.Token);
        InputRules.ValidatePlan(revised);
        Assert.Contains("csv", JsonSerializer.Serialize(revised).ToLowerInvariant());
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> messages = new();

        public string[] Messages => messages.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }

    private sealed class RecordingGenerator(DraftPlan result) : IPlanGenerator
    {
        public string? Input { get; private set; }

        public Task<DraftPlan> GenerateAsync(string input, CancellationToken cancellationToken)
        {
            Input = input;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingCollaborationGenerator(CollaborationSynthesis result)
        : IDeveloperCollaborationGenerator
    {
        public string? Input { get; private set; }

        public Task<CollaborationSynthesis> SynthesizeDeveloperCollaborationAsync(
            string input, CancellationToken cancellationToken)
        {
            Input = input;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingFeatureSpecificationGenerator(FeatureSpecification result)
        : IFeatureSpecificationGenerator
    {
        public string? Input { get; private set; }

        public Task<FeatureSpecification> GenerateFeatureSpecificationAsync(
            string input, CancellationToken cancellationToken)
        {
            Input = input;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingCodeChangeGenerator(CodeChangeProposal result) : ICodeChangeGenerator
    {
        public int Calls { get; private set; }

        public Task<CodeChangeProposal> GenerateCodeChangeAsync(string input, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class UnexpectedCatalogGenerator : IRepositoryCatalogGenerator
    {
        public Task<RepositoryFunctionality[]> GenerateCatalogAsync(
            string input, CancellationToken cancellationToken) =>
            throw new AssertFailedException("The pre-pinned catalog should prevent catalog generation.");
    }

    private static async Task<string> RunTestGitAsync(string workingDirectory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start Git in the integration test.");
        }
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode, error);
        return output;
    }

    private static void DeleteTestDirectory(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(path, recursive: true);
    }
}
