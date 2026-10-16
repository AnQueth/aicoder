using Collaborator.Core;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Collaborator.Infrastructure;

public sealed partial class RunStore
{
    public const string DeveloperProposalStage = "developer-proposals";
    private const string ProposalRoundKey = "implementation-proposals";

    /// <summary>Adds opt-in settings in the same transaction as feature intake.</summary>
    public void ConfigurePlanning(Guid id, string? mode)
    {
        if (mode is null)
        {
            return;
        }
        if (!DeveloperPlanningModes.IsKnown(mode))
        {
            throw new ArgumentException("Unknown developer planning mode.", nameof(mode));
        }
        db.WorkflowArtifacts.Add(new WorkflowArtifactRecord
        {
            RunId = id, Stage = DeveloperProposalStage, ArtifactKey = "settings",
            Revision = 1, ArtifactType = "developer-planning-settings",
            PayloadJson = JsonSerializer.Serialize(mode, JsonSerializerOptions.Web)
        });
    }

    /// <summary>Null preserves the original workflow for existing runs and clients.</summary>
    public async Task<string?> GetPlanningModeAsync(Guid id, CancellationToken ct)
    {
        var settings = await db.WorkflowArtifacts.AsNoTracking().SingleOrDefaultAsync(
            x => x.RunId == id && x.Stage == DeveloperProposalStage && x.ArtifactKey == "settings", ct);
        return settings is null ? null :
            JsonSerializer.Deserialize<string>(settings.PayloadJson, JsonSerializerOptions.Web)
                ?? throw new InvalidDataException("Developer planning settings are invalid.");
    }

    /// <summary>Returns proposal history including pending drafts that need fresh confirmation.</summary>
    public async Task<DeveloperProposalRound[]> GetProposalRoundsAsync(Guid id, CancellationToken ct)
    {
        var records = await db.WorkflowArtifacts.AsNoTracking()
            .Where(x => x.RunId == id && x.Stage == DeveloperProposalStage && x.ArtifactKey == ProposalRoundKey)
            .OrderBy(x => x.Revision).ToArrayAsync(ct);
        return records.Select(ReadRound).ToArray();
    }

    private static DeveloperProposalRound ReadRound(WorkflowArtifactRecord record) =>
        JsonSerializer.Deserialize<DeveloperProposalRound>(record.PayloadJson, JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Developer proposal round is invalid.");

    /// <summary>Snapshots a fixed reviewer group and opens input for the next plan revision.</summary>
    public async Task PrepareProposalRoundAsync(
        Guid id, int revision, DeveloperReviewerMatch[] matches, CancellationToken ct)
    {
        var run = await GetAsync(id, ct);
        var mode = await GetPlanningModeAsync(id, ct);
        if (await db.WorkflowArtifacts.AnyAsync(x => x.RunId == id && x.Stage == DeveloperProposalStage &&
            x.ArtifactKey == ProposalRoundKey && x.Revision == revision, ct))
        {
            return;
        }
        if (!DeveloperPlanningModes.IsKnown(mode) || revision != run.PlanRevision + 1 ||
            revision > InputRules.MaximumPlanRevisions)
        {
            throw new InvalidOperationException("Proposals require the next plan revision of an opted-in run.");
        }
        if (run.PlanRevision > 0 && !(mode == DeveloperPlanningModes.DraftFirst && run.PlanRevision == 1))
        {
            var previousPlan = await GetRevisionAsync(id, run.PlanRevision, ct);
            if (previousPlan?.Decision != PlanDecisionKinds.Revise)
            {
                throw new InvalidOperationException("Fresh developer input requires a plan revision request.");
            }
        }
        var persistedMatches = await GetSelectedReviewersAsync(id, ct);
        if (persistedMatches is not null)
        {
            matches = persistedMatches;
        }
        if (matches.Length != run.ReviewerCount || matches.Select(x => x.UserId).Distinct().Count() != matches.Length)
        {
            throw new InvalidOperationException("Every selected reviewer must participate in implementation proposals.");
        }
        var previous = (await GetProposalRoundsAsync(id, ct)).LastOrDefault();
        var profiles = await db.Developers.AsNoTracking().Where(x => matches.Select(m => m.UserId).Contains(x.UserId))
            .ToDictionaryAsync(x => x.UserId, ct);
        var developers = matches.Select(match =>
        {
            if (!profiles.TryGetValue(match.UserId, out var profile) ||
                persistedMatches is null && (!profile.IsEnabled || match.MatchedRoles.Length == 0 ||
                    match.MatchedRoles.Any(role => !(JsonSerializer.Deserialize<string[]>(profile.RolesJson,
                        JsonSerializerOptions.Web) ?? []).Contains(role, StringComparer.OrdinalIgnoreCase))))
            {
                throw new InvalidOperationException($"Selected developer '{match.UserId}' has an invalid profile or role match.");
            }
            var saved = previous?.Developers.SingleOrDefault(x => x.UserId == match.UserId);
            return new DeveloperProposal(match.UserId, saved?.DisplayName ?? profile.DisplayName,
                match.MatchedRoles, saved?.ReviewFocus ?? profile.ReviewFocus, saved?.Proposal,
                saved?.NoAdditionalInput ?? false, null);
        }).ToArray();
        var round = new DeveloperProposalRound(revision, "collecting", developers);
        db.WorkflowArtifacts.Add(new WorkflowArtifactRecord
        {
            RunId = id, Stage = DeveloperProposalStage, ArtifactKey = ProposalRoundKey,
            Revision = revision, ArtifactType = "developer-proposal-round",
            PayloadJson = JsonSerializer.Serialize(round, JsonSerializerOptions.Web),
            RepositoryCommit = run.RepositoryCommit
        });
        run.SelectedReviewersJson ??= JsonSerializer.Serialize(matches, JsonSerializerOptions.Web);
        run.Status = "awaiting_developer_proposals";
        AddEvent(id, "developer_proposals_requested", "workflow");
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Confirms one participant's input, freezing the whole round when everyone has responded.</summary>
    public async Task SubmitDeveloperProposalAsync(
        Guid id, string userId, DeveloperProposalSubmission submission, CancellationToken ct)
    {
        if (!DeveloperProposalRules.IsValid(submission))
        {
            throw new ArgumentException("Submit a proposal of at most 4000 characters or explicitly choose no additional input.");
        }
        var run = await GetAsync(id, ct);
        var record = await db.WorkflowArtifacts.SingleOrDefaultAsync(x => x.RunId == id &&
            x.Stage == DeveloperProposalStage && x.ArtifactKey == ProposalRoundKey &&
            x.Revision == submission.Revision, ct)
            ?? throw new InvalidOperationException("This proposal round does not exist.");
        var round = ReadRound(record);
        var developer = round.Developers.SingleOrDefault(x => x.UserId == userId)
            ?? throw new KeyNotFoundException("The developer is not assigned to this proposal round.");
        var text = submission.NoAdditionalInput ? null : submission.Proposal!.Trim();
        if (developer.SubmittedAt is not null && developer.Proposal == text &&
            developer.NoAdditionalInput == submission.NoAdditionalInput)
        {
            return;
        }
        if (run.Status != "awaiting_developer_proposals" || round.Status != "collecting" ||
            submission.Revision != run.PlanRevision + 1)
        {
            throw new InvalidOperationException("This proposal round is closed or is not the current revision.");
        }
        var developers = round.Developers.Select(x => x.UserId == userId
            ? x with { Proposal = text, NoAdditionalInput = submission.NoAdditionalInput, SubmittedAt = DateTimeOffset.UtcNow }
            : x).ToArray();
        var complete = developers.All(x => x.SubmittedAt is not null);
        record.PayloadJson = JsonSerializer.Serialize(round with
        {
            Developers = developers, Status = complete ? "ready" : "collecting"
        }, JsonSerializerOptions.Web);
        if (complete)
        {
            record.Status = WorkflowArtifactStatuses.Approved;
            run.Status = "developer_proposals_ready";
            db.Outbox.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(), RunId = id, Kind = "proposals-ready", Revision = submission.Revision
            });
        }
        AddEvent(id, "developer_proposal_submitted", userId);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Only confirmed, frozen inputs may ground the generated plan.</summary>
    public async Task<DeveloperProposal[]?> GetDeveloperProposalsForPlanAsync(Guid id, int revision, CancellationToken ct)
    {
        var mode = await GetPlanningModeAsync(id, ct);
        if (mode is null || mode == DeveloperPlanningModes.DraftFirst && revision == 1)
        {
            return null;
        }
        var round = (await GetProposalRoundsAsync(id, ct)).SingleOrDefault(x => x.Revision == revision);
        if (round is null || round.Status != "ready" || round.Developers.Any(x => x.SubmittedAt is null))
        {
            throw new InvalidOperationException("Every developer must confirm implementation input before the AI creates this plan.");
        }
        return round.Developers;
    }
}
