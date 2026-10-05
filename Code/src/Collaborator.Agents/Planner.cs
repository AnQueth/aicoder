using Collaborator.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Collaborator.Agents;

/// <summary>Generates and validates a draft using the configured planning provider.</summary>
public sealed class Planner(IPlanGenerator generator)
{
    public Task<DraftPlan> CreateAsync(
        string brief, string featureText, CancellationToken cancellationToken) =>
        CreateAsync(brief, featureText, null, null, 1, cancellationToken);

    public async Task<DraftPlan> CreateAsync(
        string brief, string featureText, DraftPlan? previousPlan, string? reviewerFeedback,
        int revision, CancellationToken cancellationToken)
    {
        var input = JsonSerializer.Serialize(new
        {
            productBrief = brief,
            requestedFeature = featureText,
            revision,
            previousPlan,
            reviewerFeedback
        }, JsonSerializerOptions.Web);
        var plan = await generator.GenerateAsync(input, cancellationToken);
        InputRules.ValidatePlan(plan);
        return plan;
    }
}

/// <summary>A provider of draft plans from serialized, untrusted request data.</summary>
public interface IPlanGenerator
{
    /// <summary>Generates a draft; provider and output errors propagate to the workflow.</summary>
    Task<DraftPlan> GenerateAsync(string input, CancellationToken cancellationToken);
}

/// <summary>Registers explicit offline or GitHub Copilot SDK planning behavior.</summary>
public static class AgentRegistration
{
    public static IServiceCollection AddPlanner(this IServiceCollection services, IConfiguration config)
    {
        var mode = config["Agents:Mode"];
        switch (mode)
        {
            case "Offline":
                services.AddSingleton<IPlanGenerator, OfflinePlanGenerator>();
                break;
            case "Copilot":
                services.AddSingleton<IPlanGenerator>(provider =>
                    new CopilotPlanGenerator(config, provider.GetRequiredService<ILogger<CopilotPlanGenerator>>()));
                break;
            default:
                throw new InvalidOperationException("Agents:Mode must be Offline or Copilot.");
        }
        services.AddSingleton<Planner>();
        return services;
    }
}

/// <summary>Deterministic local double; never represents a verified implementation plan.</summary>
public sealed class OfflinePlanGenerator : IPlanGenerator
{
    public Task<DraftPlan> GenerateAsync(string input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(input);
        var root = document.RootElement;
        var revision = root.GetProperty("revision").GetInt32();
        var feedback = root.GetProperty("reviewerFeedback").GetString();
        var plan = string.IsNullOrWhiteSpace(feedback)
            ? new DraftPlan(
                "Offline workflow demonstration only; no model or repository analysis was performed.",
                ["Review the submitted feature intent.", "Obtain repository evidence and acceptance criteria before implementation."],
                ["Which components are affected?", "What acceptance evidence is required?"])
            : new DraftPlan(
                $"Offline workflow demonstration only (revision {revision}); no model or repository analysis was performed.",
                [
                    "Review the submitted feature intent.",
                    $"Address reviewer feedback: {Truncate(feedback, 300)}",
                    "Obtain repository evidence and acceptance criteria before implementation."
                ],
                ["Does this revision resolve the reviewer feedback?", "What acceptance evidence is required?"]);
        return Task.FromResult(plan);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value.Trim() : value[..max].Trim() + "...";
}
