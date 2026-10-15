using System.Net;
using System.Text.Json;
using Collaborator.ApiClient;

namespace Collaborator.Tests;

[TestClass]
[DoNotParallelize]
public sealed class RepositoryOptionsTests
{
    [TestMethod]
    [DataRow("https://example.com/product.git")]
    [DataRow("ssh://git@example.com/product.git")]
    [DataRow("file:///H:/src/product.git")]
    public void Parse_RepositoryAndBaseBranch_ArePassedThrough(string repositoryUrl)
    {
        var options = ClientOptions.Parse([
            "--repository-url", repositoryUrl, "--base-branch", "release/stable", "--auto"
        ]);

        Assert.AreEqual(repositoryUrl, options.ProductRepositoryUrl);
        Assert.AreEqual("release/stable", options.RepositoryBaseBranch);
    }

    [TestMethod]
    public void Parse_EnvironmentDefaults_CanBeOverriddenByFlags()
    {
        var oldUrl = Environment.GetEnvironmentVariable("COLLABORATOR_PRODUCT_REPOSITORY_URL");
        var oldBranch = Environment.GetEnvironmentVariable("COLLABORATOR_PRODUCT_BASE_BRANCH");
        try
        {
            Environment.SetEnvironmentVariable("COLLABORATOR_PRODUCT_REPOSITORY_URL", "https://example.com/product.git");
            Environment.SetEnvironmentVariable("COLLABORATOR_PRODUCT_BASE_BRANCH", "develop");
            var defaults = ClientOptions.Parse(["--auto"]);
            var overridden = ClientOptions.Parse(["--base-branch", "main", "--auto"]);

            Assert.AreEqual("develop", defaults.RepositoryBaseBranch);
            Assert.AreEqual("main", overridden.RepositoryBaseBranch);
        }
        finally
        {
            Environment.SetEnvironmentVariable("COLLABORATOR_PRODUCT_REPOSITORY_URL", oldUrl);
            Environment.SetEnvironmentVariable("COLLABORATOR_PRODUCT_BASE_BRANCH", oldBranch);
        }
    }

    [TestMethod]
    [DataRow("--bad")]
    [DataRow("main~1")]
    [DataRow("")]
    public void Parse_InvalidBaseBranch_IsRejected(string branch) =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            ClientOptions.Parse(["--repository-url", "https://example.com/product.git", "--base-branch", branch]));

    [TestMethod]
    public async Task UpdateRepository_SendsBaseBranch()
    {
        using var handler = new RepositoryRequestHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var api = new CollaboratorApi(http, null);

        var result = await api.UpdateProductRepositoryAsync(
            "sample", "https://example.com/product.git", CancellationToken.None, "develop");

        Assert.IsTrue(result.IsSuccess);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.AreEqual("develop", body.RootElement.GetProperty("repositoryBaseBranch").GetString());
        Assert.AreEqual("https://example.com/product.git", body.RootElement.GetProperty("repositoryUrl").GetString());
    }

    private sealed class RepositoryRequestHandler : HttpMessageHandler
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
