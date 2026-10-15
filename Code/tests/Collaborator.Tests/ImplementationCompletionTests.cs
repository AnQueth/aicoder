using Collaborator.Agents;

namespace Collaborator.Tests;

[TestClass]
public sealed class ImplementationCompletionTests
{
    [TestMethod]
    public async Task CompleteImplementation_ProgressCheckIn_ContinuesSameSessionUntilFinished()
    {
        var prompts = new List<string>();
        var progress = new List<string>();
        var result = await CopilotPlanGenerator.CompleteImplementationAsync("Approved plan", (prompt, _) =>
        {
            prompts.Add(prompt);
            return Task.FromResult(prompts.Count == 1
                ? """{"summary":"Dependency installed; UI and tests remain.","complete":false}"""
                : """{"summary":"Feature, tests and docs finished.","complete":true}""");
        }, progress.Add, CancellationToken.None);

        Assert.HasCount(2, prompts);
        Assert.AreEqual("Approved plan", prompts[0]);
        Assert.Contains("same approved plan", prompts[1]);
        Assert.HasCount(1, progress);
        Assert.AreEqual("Feature, tests and docs finished.", result);
    }

    [TestMethod]
    [DataRow("""{"summary":"Progress check-in"}""")]
    [DataRow("""{"summary":"","complete":true}""")]
    [DataRow("""{"summary":"Done","complete":"true"}""")]
    public async Task CompleteImplementation_InvalidCompletion_FailsRatherThanPublishing(string response)
    {
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            CopilotPlanGenerator.CompleteImplementationAsync("Plan",
                (_, _) => Task.FromResult(response), _ => Assert.Fail("Invalid response is not progress."),
                CancellationToken.None));
    }

    [TestMethod]
    public async Task CompleteImplementation_UnfinishedAtDeadline_CannotReturnPartialSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            CopilotPlanGenerator.CompleteImplementationAsync("Plan", (_, _) =>
            {
                calls++;
                return Task.FromResult("""{"summary":"Still working.","complete":false}""");
            }, _ => cancellation.Cancel(), cancellation.Token));
        Assert.AreEqual(1, calls);
    }
}
