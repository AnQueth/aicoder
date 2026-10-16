using Collaborator.Infrastructure;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Collaborator.Api;

/// <summary>A bounded page of directory entries; total counts entries before pagination.</summary>
public sealed record DirectoryPage<T>(T[] Items, int Total, int Offset, int Limit);

/// <summary>Product context and repository settings without the stored repository catalog.</summary>
public sealed record ProductListEntry(
    string Id, string Name, string Brief, string? RepositoryUrl, string? RepositoryBaseBranch);

/// <summary>Run discovery metadata without captured prompts, idempotency keys, or artifact payloads.</summary>
public sealed record RunListEntry(
    Guid Id, string ProductId, string Text, string Status, DateTimeOffset CreatedAt);

/// <summary>Read-only directory routes used by the browser client.</summary>
public static class DirectoryEndpoints
{
    /// <summary>Registers paginated product and run listings on the existing authenticated API group.</summary>
    /// <param name="api">The versioned API route group.</param>
    public static void MapDirectoryEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/products", async Task<Results<Ok<DirectoryPage<ProductListEntry>>, ProblemHttpResult>> (
            int? offset, int? limit, CollaboratorDb db, CancellationToken ct) =>
        {
            var skip = offset ?? 0;
            var take = limit ?? 50;
            if (skip < 0 || take is < 1 or > 200)
            {
                return InvalidPage();
            }
            var query = db.Products.AsNoTracking();
            var total = await query.CountAsync(ct);
            var items = await query.OrderBy(product => product.Id).Skip(skip).Take(take)
                .Select(product => new ProductListEntry(
                    product.Id, product.Name, product.Brief, product.RepositoryUrl, product.RepositoryBaseBranch))
                .ToArrayAsync(ct);
            return TypedResults.Ok(new DirectoryPage<ProductListEntry>(items, total, skip, take));
        })
            .WithName("ListProducts")
            .WithSummary("Browse registered products and repository settings")
            .WithDescription("Returns a page ordered by product ID. Offset defaults to 0; limit defaults to 50 and must be 1-200.");

        api.MapGet("/runs", async Task<Results<Ok<DirectoryPage<RunListEntry>>, ProblemHttpResult>> (
            int? offset, int? limit, CollaboratorDb db, CancellationToken ct) =>
        {
            var skip = offset ?? 0;
            var take = limit ?? 50;
            if (skip < 0 || take is < 1 or > 200)
            {
                return InvalidPage();
            }
            var query = db.Runs.AsNoTracking();
            var total = await query.CountAsync(ct);
            var items = await query.OrderByDescending(run => run.CreatedAt).ThenBy(run => run.Id)
                .Skip(skip).Take(take)
                .Select(run => new RunListEntry(run.Id, run.ProductId, run.Text, run.Status, run.CreatedAt))
                .ToArrayAsync(ct);
            return TypedResults.Ok(new DirectoryPage<RunListEntry>(items, total, skip, take));
        })
            .WithName("ListRuns")
            .WithSummary("Browse feature runs, newest first")
            .WithDescription("Returns summary metadata, not captured prompts or workflow internals. Offset defaults to 0; limit defaults to 50 and must be 1-200.");
    }

    private static ProblemHttpResult InvalidPage() => TypedResults.Problem(
        statusCode: 400, title: "Offset must be nonnegative and limit must be between 1 and 200.");
}
