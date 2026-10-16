using System.Net;
using System.Net.Http.Json;
using Collaborator.Api;
using Collaborator.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Collaborator.Tests;

[TestClass]
public sealed class DirectoryEndpointsTests
{
    [TestMethod]
    public async Task ProductsArePagedInIdOrderWithoutCatalogPayloads()
    {
        await using var host = await DirectoryHost.StartAsync();
        using var response = await host.Client.GetAsync("/api/v1/products?offset=1&limit=1");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<DirectoryPage<ProductListEntry>>();
        Assert.IsNotNull(page);
        Assert.AreEqual(3, page.Total);
        Assert.AreEqual(1, page.Offset);
        Assert.AreEqual(1, page.Limit);
        Assert.HasCount(1, page.Items);
        Assert.AreEqual("beta", page.Items[0].Id);
        Assert.IsFalse((await response.Content.ReadAsStringAsync()).Contains("repositoryCatalog", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RunsAreNewestFirstAndDoNotExposeCapturedPromptsOrKeys()
    {
        await using var host = await DirectoryHost.StartAsync();
        using var response = await host.Client.GetAsync("/api/v1/runs");
        var page = await response.Content.ReadFromJsonAsync<DirectoryPage<RunListEntry>>();
        Assert.IsNotNull(page);
        Assert.AreEqual(0, page.Offset);
        Assert.AreEqual(50, page.Limit);
        Assert.AreEqual(3, page.Total);
        Assert.AreEqual("Newest feature", page.Items[0].Text);
        Assert.AreEqual("Middle feature", page.Items[1].Text);
        var json = await response.Content.ReadAsStringAsync();
        Assert.IsFalse(json.Contains("idempotencyKey", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("businessAnalystPrompt", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("productBrief", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("/products?offset=-1")]
    [DataRow("/products?limit=0")]
    [DataRow("/products?limit=201")]
    [DataRow("/runs?offset=-1")]
    [DataRow("/runs?limit=0")]
    [DataRow("/runs?limit=201")]
    public async Task InvalidPaginationReturnsProblemDetails(string path)
    {
        await using var host = await DirectoryHost.StartAsync();
        using var response = await host.Client.GetAsync("/api/v1" + path);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "Offset must be nonnegative");
    }

    [TestMethod]
    [DataRow("/products?offset=100&limit=200")]
    [DataRow("/runs?offset=100&limit=200")]
    public async Task OffsetBeyondDirectoryReturnsAnEmptyPageWithTotal(string path)
    {
        await using var host = await DirectoryHost.StartAsync();
        using var response = await host.Client.GetAsync("/api/v1" + path);
        var page = await response.Content.ReadFromJsonAsync<DirectoryPage<System.Text.Json.JsonElement>>();
        Assert.IsNotNull(page);
        Assert.AreEqual(3, page.Total);
        Assert.HasCount(0, page.Items);
    }

    private sealed class DirectoryHost(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client => client;

        public static async Task<DirectoryHost> StartAsync()
        {
            var databaseName = Guid.NewGuid().ToString();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddDbContext<CollaboratorDb>(options => options.UseInMemoryDatabase(databaseName));
            var app = builder.Build();
            app.MapGroup("/api/v1").MapDirectoryEndpoints();
            using (var scope = app.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CollaboratorDb>();
                db.Products.AddRange(new[] { "gamma", "alpha", "beta" }.Select(id => new Product
                {
                    Id = id, Name = id, Brief = "Product context", RepositoryUrl = "https://example.test/repo.git",
                    RepositoryCatalogJson = "{\"private\":\"catalog\"}"
                }));
                var now = DateTimeOffset.UtcNow;
                db.Runs.AddRange(new[] { "Oldest feature", "Middle feature", "Newest feature" }
                    .Select((text, index) => new FeatureRun
                    {
                        Id = Guid.NewGuid(), ProductId = "alpha", Text = text, ProductBrief = "Captured brief",
                        IdempotencyKey = $"key-{index}", BusinessAnalystId = "default",
                        BusinessAnalystSkillId = "requirements-analysis", BusinessAnalystSkillName = "Requirements",
                        BusinessAnalystPrompt = "Captured prompt", CreatedAt = now.AddMinutes(index)
                    }));
                await db.SaveChangesAsync();
            }
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            return new DirectoryHost(app, new HttpClient { BaseAddress = new Uri(address) });
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
