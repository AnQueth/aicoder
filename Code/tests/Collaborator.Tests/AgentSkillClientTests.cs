using System.Net;
using System.Text;
using System.Text.Json;
using Collaborator.ApiClient;
using Collaborator.Core;

namespace Collaborator.Tests;

[TestClass]
public sealed class AgentSkillClientTests
{
    [TestMethod]
    public async Task GetAgentSkills_BusinessAnalyst_UsesSharedRegistry()
    {
        using var handler = new SkillRequestHandler(
            """[{"roleId":"business-analyst","domain":"general","skillId":"requirements-analysis","name":"Requirements","prompt":"Clarify outcomes.","activeRevision":3,"isEnabled":true}]""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var api = new CollaboratorApi(http, null);

        var result = await api.GetAgentSkillsAsync("business-analyst", "general", CancellationToken.None);

        Assert.AreEqual(HttpMethod.Get, handler.Method);
        Assert.AreEqual("/api/v1/agent-skills?roleId=business-analyst&domain=general", handler.Path);
        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(result.Body);
        var skill = Assert.ContainsSingle(result.Body);
        Assert.AreEqual(3, skill.ActiveRevision);
        Assert.AreEqual("requirements-analysis", skill.SkillId);
    }

    [TestMethod]
    public async Task CreateAgentSkillRevision_BusinessAnalyst_PostsRevisionWithActor()
    {
        using var handler = new SkillRequestHandler("{}");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var api = new CollaboratorApi(http, null);
        var request = new AgentSkillRevisionRequest(
            "Requirements", "Clarify outcomes.", ChangeReason: "Updated prompt.", ActorId: "ba-team");

        var result = await api.CreateAgentSkillRevisionAsync(
            "business-analyst", "general", "requirements-analysis", request, CancellationToken.None);

        Assert.AreEqual(HttpMethod.Post, handler.Method);
        Assert.AreEqual("/api/v1/agent-skills/business-analyst/general/requirements-analysis/revisions", handler.Path);
        Assert.IsTrue(result.IsSuccess);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.AreEqual("ba-team", body.RootElement.GetProperty("actorId").GetString());
        Assert.AreEqual("Clarify outcomes.", body.RootElement.GetProperty("prompt").GetString());
        Assert.IsTrue(body.RootElement.GetProperty("isEnabled").GetBoolean());
        Assert.IsTrue(InputRules.IsValidAgentSkillRevision(
            "business-analyst", "general", "requirements-analysis", request, out _));
    }

    private sealed class SkillRequestHandler(string response) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Path { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri!.PathAndQuery;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }
    }
}
