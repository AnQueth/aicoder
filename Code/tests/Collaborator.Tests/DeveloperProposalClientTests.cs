using System.Net;
using System.Text.Json;
using Collaborator.ApiClient;
using Collaborator.Core;

namespace Collaborator.Tests;

[TestClass]
public sealed class DeveloperProposalClientTests
{
    [TestMethod]
    [DataRow("proposals-first")]
    [DataRow("draft-first")]
    public void ParsesModeAndRevisionSpecificInputs(string mode)
    {
        var options = ClientOptions.Parse(["--planning-mode", mode,
            "--implementation-proposal", "1|alice|Use service | preserve encoding",
            "--no-implementation-input", "1|bob",
            "--implementation-proposal", "2|alice|Add retries", "--auto"]);
        Assert.AreEqual(mode, options.PlanningMode);
        Assert.AreEqual("Use service | preserve encoding", options.ImplementationInputs[(1, "alice")].Proposal);
        Assert.IsTrue(options.ImplementationInputs[(1, "bob")].NoAdditionalInput);
        Assert.AreEqual("Add retries", options.ImplementationInputs[(2, "alice")].Proposal);
        Assert.IsFalse(options.Interactive);
    }

    [TestMethod]
    [DataRow("--planning-mode", "unknown")]
    [DataRow("--implementation-proposal", "1|alice|")]
    [DataRow("--implementation-proposal", "0|alice|Idea")]
    [DataRow("--no-implementation-input", "1|")]
    public void RejectsInvalidOptions(string option, string value)
    {
        Assert.ThrowsExactly<ArgumentException>(() => ClientOptions.Parse(["--planning-mode", "proposals-first", option, value]));
    }

    [TestMethod]
    public void RejectsImplicitLegacyInputsAndDuplicateConfirmations()
    {
        Assert.ThrowsExactly<ArgumentException>(() => ClientOptions.Parse(["--no-implementation-input", "1|alice"]));
        Assert.ThrowsExactly<ArgumentException>(() => ClientOptions.Parse(["--planning-mode", "draft-first",
            "--no-implementation-input", "2|alice", "--implementation-proposal", "2|alice|Conflicting"]));
        Assert.ThrowsExactly<ArgumentException>(() => ClientOptions.Parse(["--run", Guid.NewGuid().ToString(),
            "--planning-mode", "draft-first"]));
        var resumed = ClientOptions.Parse(["--run", Guid.NewGuid().ToString(), "--no-implementation-input", "3|bob"]);
        Assert.IsTrue(resumed.ImplementationInputs[(3, "bob")].NoAdditionalInput);
    }

    [TestMethod]
    public async Task SubmitsPlanningModeAndExactProposalPayloads()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var api = new CollaboratorApi(http, null);
        var id = Guid.NewGuid();
        await api.SubmitFeatureAsync("sample", "Feature", "key", "ba", "analysis", 2, default,
            planningMode: DeveloperPlanningModes.DraftFirst);
        using (var body = JsonDocument.Parse(handler.Bodies[0]))
        {
            Assert.AreEqual("draft-first", body.RootElement.GetProperty("planningMode").GetString());
        }
        var inputs = new Dictionary<(int, string), DeveloperProposalSubmission>
        {
            [(2, "alice")] = new(2, "Reuse existing service"), [(2, "bob")] = new(2, null, true)
        };
        var progress = new List<string>();
        await DeveloperProposalClient.SubmitRoundAsync(api, id, Round(2), inputs, progress.Add, default);
        Assert.AreEqual($"/api/v1/runs/{id}/developer-proposals/alice", handler.Paths[1]);
        using var alice = JsonDocument.Parse(handler.Bodies[1]);
        using var bob = JsonDocument.Parse(handler.Bodies[2]);
        Assert.AreEqual(2, alice.RootElement.GetProperty("revision").GetInt32());
        Assert.AreEqual("Reuse existing service", alice.RootElement.GetProperty("proposal").GetString());
        Assert.IsTrue(bob.RootElement.GetProperty("noAdditionalInput").GetBoolean());
        Assert.IsTrue(progress.Any(line => line.Contains("2/2") && line.Contains("accepted")));
    }

    [TestMethod]
    public async Task MissingFreshRevisionInputFailsBeforeAnyWrites()
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var inputs = new Dictionary<(int, string), DeveloperProposalSubmission>
        {
            [(1, "alice")] = new(1, "Old idea"), [(1, "bob")] = new(1, null, true),
            [(2, "alice")] = new(2, "New idea")
        };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => DeveloperProposalClient.SubmitRoundAsync(
            new CollaboratorApi(http, null), Guid.NewGuid(), Round(2), inputs, _ => { }, default));
        Assert.IsEmpty(handler.Paths);
    }

    [TestMethod]
    public async Task ResumeSkipsConfirmedInputsAndReportsServerErrors()
    {
        var handler = new RecordingHandler { Status = HttpStatusCode.Conflict };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var round = Round(3);
        round.Developers[0] = round.Developers[0] with { SubmittedAt = DateTimeOffset.UtcNow };
        var inputs = new Dictionary<(int, string), DeveloperProposalSubmission> { [(3, "bob")] = new(3, null, true) };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => DeveloperProposalClient.SubmitRoundAsync(
            new CollaboratorApi(http, null), Guid.NewGuid(), round, inputs, _ => { }, default));
        Assert.HasCount(1, handler.Paths);
        StringAssert.EndsWith(handler.Paths[0], "/bob");
    }

    private static DeveloperProposalRound Round(int revision) => new(revision, "collecting",
        [new("alice", "Alice", ["backend"], null, "Prior text is not confirmed", false, null),
         new("bob", "Bob", ["testing"], null, null, false, null)]);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public List<string> Bodies { get; } = [];
        public HttpStatusCode Status { get; init; } = HttpStatusCode.Accepted;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(Status);
        }
    }
}
