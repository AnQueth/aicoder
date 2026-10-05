using Collaborator.Agents;
using Collaborator.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        using var services = new ServiceCollection().AddLogging().AddPlanner(config).BuildServiceProvider();
        Assert.IsInstanceOfType<CopilotPlanGenerator>(services.GetRequiredService<IPlanGenerator>());
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
}
