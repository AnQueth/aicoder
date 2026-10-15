using System.Text.Json;
using Collaborator.ApiClient;
using Collaborator.Core;

namespace Collaborator.Tests;

[TestClass]
public sealed class ReviewHandoffTests
{
    [TestMethod]
    public void Format_ProvidesReviewLocationCommandsAndPublicationLimits()
    {
        var runId = Guid.Parse("14cb25fd-6483-4161-a9dd-b39a466d5610");
        var change = new CodeChangeStatus(
            "ready", "Added emailing.", $"collaborator/feature-{runId:N}", new string('a', 40),
            ["Invoicing/InvoiceEmailService.cs", "Invoicing.Tests/EmailTests.cs"], null);

        var output = ReviewHandoff.Format(
            runId, "file:///H:/src/sample-remote.git", new string('b', 40),
            change, @"H:\reviews\invoice review");

        Assert.Contains("repository      : file:///H:/src/sample-remote.git", output);
        Assert.Contains($"review branch   : {change.BranchName}", output);
        Assert.Contains($"review commit   : {change.Commit}", output);
        Assert.Contains("Invoicing/InvoiceEmailService.cs", output);
        Assert.Contains("Invoicing.Tests/EmailTests.cs", output);
        Assert.Contains($"git clone --single-branch --branch '{change.BranchName}' -- 'file:///H:/src/sample-remote.git' 'H:\\reviews\\invoice review'", output);
        Assert.Contains($"diff '{new string('b', 40)}' '{change.Commit}' --", output);
        Assert.Contains("code 'H:\\reviews\\invoice review'", output);
        Assert.Contains("nothing was merged", output);
        Assert.Contains("has not created a local review checkout", output);
        Assert.Contains("not that build/tests passed", output);
    }

    [TestMethod]
    public void Format_EscapesPowerShellQuotes()
    {
        var output = ReviewHandoff.Format(
            Guid.NewGuid(), "file:///H:/src/sample-remote.git", null,
            new CodeChangeStatus("ready", null, "review/invoice", new string('a', 40), [], null),
            @"H:\Aaron's reviews\invoice");

        Assert.Contains(@"'H:\Aaron''s reviews\invoice'", output);
        Assert.DoesNotContain(@"git -C 'H:\Aaron''s reviews\invoice' diff", output);
    }

    [TestMethod]
    public void Format_MissingRepositoryDoesNotInventCheckoutCommands()
    {
        var output = ReviewHandoff.Format(
            Guid.NewGuid(), null, null,
            new CodeChangeStatus("ready", null, "review/invoice", new string('a', 40), [], null),
            @"H:\reviews\invoice");

        Assert.Contains("repository      : (not provided by API)", output);
        Assert.Contains("Review checkout commands unavailable", output);
        Assert.DoesNotContain("git clone", output);
    }

    [TestMethod]
    public void RunView_ReadsPinnedRepositoryUrl()
    {
        var run = JsonSerializer.Deserialize<RunView>(
            """{"id":"14cb25fd-6483-4161-a9dd-b39a466d5610","repositoryUrl":"file:///H:/src/sample-remote.git","repositoryBaseBranch":"develop"}""",
            JsonSerializerOptions.Web);

        Assert.IsNotNull(run);
        Assert.AreEqual("file:///H:/src/sample-remote.git", run.RepositoryUrl);
        Assert.AreEqual("develop", run.RepositoryBaseBranch);
    }
}
