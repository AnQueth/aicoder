using Collaborator.Core;

namespace Collaborator.ApiClient;

/// <summary>Submits only explicitly supplied human inputs, never generated or implicit proposals.</summary>
internal static class DeveloperProposalClient
{
    public static async Task SubmitRoundAsync(
        CollaboratorApi api, Guid runId, DeveloperProposalRound round,
        IReadOnlyDictionary<(int Revision, string UserId), DeveloperProposalSubmission> inputs,
        Action<string> progress, CancellationToken ct)
    {
        var pending = round.Developers.Where(developer => developer.SubmittedAt is null).ToArray();
        var missing = pending.Where(developer => !inputs.ContainsKey((round.Revision, developer.UserId)))
            .Select(developer => developer.UserId).ToArray();
        if (missing.Length > 0)
        {
            throw new ArgumentException(
                $"Plan r{round.Revision} needs explicit implementation input for: {string.Join(", ", missing)}. " +
                "Resume with --run and --implementation-proposal 'revision|id|text' or --no-implementation-input 'revision|id'. " +
                "Automatic mode never invents ideas or reuses an earlier revision's confirmation.");
        }
        for (var index = 0; index < pending.Length; index++)
        {
            var developer = pending[index];
            var input = inputs[(round.Revision, developer.UserId)];
            if (input.Revision != round.Revision || !DeveloperProposalRules.IsValid(input))
            {
                throw new ArgumentException($"Invalid implementation input for {developer.UserId}, plan r{round.Revision}.");
            }
            progress($"  developer input {index + 1}/{pending.Length}: {developer.DisplayName} ({developer.UserId}), " +
                $"plan r{round.Revision}, submitting {(input.NoAdditionalInput ? "explicit no additional input" : "implementation proposal")}...");
            var result = await api.SubmitDeveloperProposalAsync(runId, developer.UserId, input, ct);
            if (!result.IsSuccess)
            {
                throw new InvalidOperationException(
                    $"Implementation input for {developer.UserId} returned {(int)result.Status}: {result.Problem}. Refresh before retrying.");
            }
            progress($"  developer input {index + 1}/{pending.Length}: {developer.UserId}, plan r{round.Revision}, accepted.");
        }
        progress($"  plan r{round.Revision}: {pending.Length} inputs submitted, {round.Developers.Length - pending.Length} already confirmed; awaiting combined AI plan.");
    }
}
