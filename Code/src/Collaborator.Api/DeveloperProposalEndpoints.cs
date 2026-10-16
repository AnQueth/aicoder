using Collaborator.Core;
using Collaborator.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Collaborator.Api;

/// <summary>Revision-bound implementation inputs, distinct from plan approvals.</summary>
public static class DeveloperProposalEndpoints
{
    /// <summary>Maps proposal history and participant confirmation routes on the protected API group.</summary>
    public static void MapDeveloperProposalEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/runs/{id:guid}/developer-proposals", async (
            Guid id, CollaboratorDb db, RunStore store, CancellationToken ct) =>
        {
            if (!await db.Runs.AnyAsync(x => x.Id == id, ct))
            {
                return Results.NotFound();
            }
            return Results.Ok(await store.GetProposalRoundsAsync(id, ct));
        }).WithSummary("List revisioned human implementation proposals");

        api.MapPost("/runs/{id:guid}/developer-proposals/{userId}", async (
            Guid id, string userId, DeveloperProposalSubmission submission,
            CollaboratorDb db, RunStore store, CancellationToken ct) =>
        {
            if (!DeveloperProposalRules.IsValid(submission))
            {
                return Results.Problem(statusCode: 400,
                    title: "Provide a target revision and either a proposal of at most 4000 characters or explicit no additional input, not both.");
            }
            if (!await db.Runs.AnyAsync(x => x.Id == id, ct))
            {
                return Results.NotFound();
            }
            try
            {
                await store.SubmitDeveloperProposalAsync(id, userId, submission, ct);
                return Results.Accepted($"/api/v1/runs/{id}/developer-proposals");
            }
            catch (KeyNotFoundException ex)
            {
                return Results.Problem(statusCode: 404, title: ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(statusCode: 409, title: ex.Message);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Problem(statusCode: 409, title: "Developer inputs changed concurrently. Refresh and resubmit your contribution.");
            }
        }).WithSummary("Confirm one developer's implementation proposal for an exact plan revision");
    }
}
