using Collaborator.Agents;
using Collaborator.Core;
using Collaborator.Infrastructure;
using Collaborator.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Text.Json;

namespace Collaborator.Tests;

[TestClass]
public sealed class FoundationTests
{
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
    public void ProductIdCannotContainPathCharacters() =>
        Assert.IsFalse(InputRules.IsProductId("../secrets"));

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
        Assert.IsTrue(InputRules.IsValidDeveloper("alice", new DeveloperConfiguration("Alice"), out _));
        Assert.IsFalse(InputRules.IsValidDeveloper(" ", new DeveloperConfiguration("Alice"), out _));
        Assert.IsFalse(InputRules.IsValidDeveloper("alice", new DeveloperConfiguration(" "), out _));
        Assert.IsFalse(InputRules.IsValidDeveloper("alice", new DeveloperConfiguration(
            "Alice", new string('x', 1_001)), out _));
    }

    [TestMethod]
    public void ReviewAssignmentResponseMustBeNonemptyAndBounded()
    {
        Assert.IsTrue(InputRules.IsValidReviewAssignmentResponse(
            new ReviewAssignmentResponse("Looks good, add a test."), out _));
        Assert.IsFalse(InputRules.IsValidReviewAssignmentResponse(new ReviewAssignmentResponse(" "), out _));
        Assert.IsFalse(InputRules.IsValidReviewAssignmentResponse(new ReviewAssignmentResponse(
            new string('x', InputRules.MaximumCommentLength + 1)), out _));
        Assert.IsFalse(InputRules.IsValidReviewAssignmentResponse(null, out _));
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
            IdempotencyKey = Guid.NewGuid().ToString("N")
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
                RepositoryUrl = repositoryUrl,
                RepositoryCommit = pinnedCommit,
                RepositoryCatalogJson = JsonSerializer.Serialize(catalog, JsonSerializerOptions.Web),
                PlanJson = JsonSerializer.Serialize(plan, JsonSerializerOptions.Web),
                PlanRevision = 1,
                Decision = PlanDecisionKinds.Approve,
                Approved = true,
                Status = "code_change_generating"
            });
            db.PlanRevisions.Add(new PlanRevisionRecord
            {
                RunId = runId,
                Revision = 1,
                PlanJson = JsonSerializer.Serialize(plan, JsonSerializerOptions.Web),
                Decision = PlanDecisionKinds.Approve
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
            Decision = PlanDecisionKinds.Reject,
            Approved = false,
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
        var catalog = new RepositoryCatalog(
            "da420a5846bbca95e4be5b7d9eb7a6f00387536d",
            DateTimeOffset.UnixEpoch,
            1,
            [new RepositoryFunctionality("Task service", "Creates tasks.", ["src/tasks.ts"], ["TaskService"])]);
        await new Planner(generator).CreateAsync(
            "Brief", "Filter tasks", null, null, 1, catalog, CancellationToken.None);
        using var document = JsonDocument.Parse(generator.Input!);
        var evidence = document.RootElement.GetProperty("repositoryCatalog");
        Assert.AreEqual(catalog.Commit, evidence.GetProperty("commit").GetString());
        Assert.AreEqual("src/tasks.ts",
            evidence.GetProperty("functionalities")[0].GetProperty("paths")[0].GetString());
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
    public async Task CopilotProducesAndRevisesValidPlan()
    {
        if (Environment.GetEnvironmentVariable("COLLABORATOR_TEST_COPILOT") != "1")
        {
            Assert.Inconclusive("Set COLLABORATOR_TEST_COPILOT=1 to run this test using Copilot access and usage.");
        }
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Agents:Mode"] = "Copilot" }).Build();
        using var services = new ServiceCollection().AddLogging(logging => logging.AddConsole())
            .AddPlanner(config).BuildServiceProvider();
        var planner = services.GetRequiredService<Planner>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(7));
        var first = await planner.CreateAsync(
            "A fictional invoice application; no repository is supplied.",
            "Allow exporting invoices.", deadline.Token);
        InputRules.ValidatePlan(first);
        var revised = await planner.CreateAsync(
            "A fictional invoice application; no repository is supplied.",
            "Allow exporting invoices.", first, "Include CSV export.", 2, deadline.Token);
        InputRules.ValidatePlan(revised);
        Assert.Contains("csv", JsonSerializer.Serialize(revised).ToLowerInvariant());
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
