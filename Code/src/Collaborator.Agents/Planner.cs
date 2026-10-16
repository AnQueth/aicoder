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
        int revision, CancellationToken cancellationToken) =>
        await CreateAsync(brief, featureText, previousPlan, reviewerFeedback, revision, null, cancellationToken);

    public async Task<DraftPlan> CreateAsync(
        string brief, string featureText, DraftPlan? previousPlan, string? reviewerFeedback,
        int revision, RepositoryCatalog? repositoryCatalog, CancellationToken cancellationToken)
        => await CreateAsync(brief, featureText, null, previousPlan, reviewerFeedback, revision,
            repositoryCatalog, cancellationToken);

    public async Task<DraftPlan> CreateAsync(
        string brief, string featureText, FeatureSpecification? approvedSpecification, DraftPlan? previousPlan,
        string? reviewerFeedback, int revision, RepositoryCatalog? repositoryCatalog,
        CancellationToken cancellationToken)
        => await CreateAsync(
            brief, featureText, approvedSpecification, previousPlan, reviewerFeedback, revision,
            repositoryCatalog, null, cancellationToken);

    public async Task<DraftPlan> CreateAsync(
        string brief, string featureText, FeatureSpecification? approvedSpecification, DraftPlan? previousPlan,
        string? reviewerFeedback, int revision, RepositoryCatalog? repositoryCatalog,
        RepositorySourceReference? repositorySource, CancellationToken cancellationToken,
        DeveloperSkillAnalysis[]? specialistAnalyses = null,
        CollaborationSynthesis? collaborationSynthesis = null,
        DeveloperProposal[]? developerProposals = null)
    {
        var input = JsonSerializer.Serialize(new
        {
            productBrief = brief,
            requestedFeature = featureText,
            approvedFeatureSpecification = approvedSpecification,
            revision,
            previousPlan,
            reviewerFeedback,
            repositoryCatalog,
            repositorySource,
            specialistAnalyses,
            collaborationSynthesis,
            developerProposals
        }, JsonSerializerOptions.Web);
        var plan = await generator.GenerateAsync(input, cancellationToken);
        InputRules.ValidatePlan(plan);
        return plan;
    }
}

/// <summary>Runs one captured developer skill against the approved requirements and repository snapshot.</summary>
public sealed class DeveloperSpecialist(IDeveloperSkillAnalysisGenerator generator)
{
    public async Task<DeveloperSkillAnalysis> AnalyzeAsync(
        DeveloperSkillSnapshot skill,
        string productBrief,
        string featureText,
        FeatureSpecification specification,
        ArchitectureAnalysis architecture,
        RepositoryCatalog? repositoryCatalog,
        RepositorySourceReference repositorySource,
        CancellationToken cancellationToken)
    {
        var input = JsonSerializer.Serialize(new
        {
            productBrief,
            requestedFeature = featureText,
            approvedFeatureSpecification = specification,
            approvedArchitectureAnalysis = architecture,
            skillName = skill.Name,
            skillPrompt = skill.Prompt,
            repositoryCatalog,
            repositorySource
        }, JsonSerializerOptions.Web);
        var draft = await generator.AnalyzeDeveloperSkillAsync(input, cancellationToken);
        return InputRules.CreateDeveloperSkillAnalysis(skill, draft);
    }
}

/// <summary>Compares every captured role result without replacing or hiding the original analyses.</summary>
public sealed class DeveloperCollaborator(IDeveloperCollaborationGenerator generator)
{
    /// <summary>Compares all specialist outputs and checks that each claim points to captured skill revisions.</summary>
    public async Task<CollaborationSynthesis> SynthesizeAsync(
        DeveloperSkillAnalysis[] analyses,
        AgentSkillRevisionReference[] capturedSkills,
        CancellationToken cancellationToken,
        RepositorySourceReference? repositorySource = null)
    {
        ArgumentNullException.ThrowIfNull(analyses);
        ArgumentNullException.ThrowIfNull(capturedSkills);
        if (analyses.Length == 0 || analyses.Length != capturedSkills.Length)
        {
            throw new InvalidDataException("Collaboration requires one analysis for every captured developer skill.");
        }
        var captured = capturedSkills.ToHashSet();
        var analyzedSkills = analyses.Select(analysis => new AgentSkillRevisionReference(
            analysis.RoleId, analysis.SkillId, analysis.Domain, analysis.SkillRevision)).ToArray();
        if (captured.Count != capturedSkills.Length ||
            analyzedSkills.Distinct().Count() != analyzedSkills.Length ||
            !captured.SetEquals(analyzedSkills))
        {
            throw new InvalidDataException(
                "Collaboration inputs must contain exactly one analysis for every captured skill revision.");
        }

        var input = JsonSerializer.Serialize(new
        {
            capturedSkills,
            specialistAnalyses = analyses,
            repositorySource
        }, JsonSerializerOptions.Web);
        var synthesis = await generator.SynthesizeDeveloperCollaborationAsync(input, cancellationToken);
        InputRules.ValidateCollaborationSynthesis(synthesis, captured);
        return synthesis;
    }
}

/// <summary>Turns submitted feature intent into a structured draft for human business review.</summary>
public sealed class BusinessAnalyst(IFeatureSpecificationGenerator generator)
{
    /// <summary>Generates and validates one formal specification draft.</summary>
    public async Task<FeatureSpecification> CreateAsync(
        string productBrief,
        string featureText,
        string skillName,
        string skillPrompt,
        FeatureSpecification? previousSpecification,
        string? analystFeedback,
        int revision,
        CancellationToken cancellationToken,
        RepositorySourceReference? repositorySource = null)
    {
        var input = JsonSerializer.Serialize(new
        {
            productBrief,
            requestedFeature = featureText,
            skillName,
            skillPrompt,
            previousSpecification,
            analystFeedback,
            revision,
            repositorySource
        }, JsonSerializerOptions.Web);
        var specification = await generator.GenerateFeatureSpecificationAsync(input, cancellationToken);
        InputRules.ValidateFeatureSpecification(specification);
        return specification;
    }
}

/// <summary>Generates an evidence-grounded architecture review for an approved feature specification.</summary>
public sealed class Architect(IArchitectureAnalysisGenerator generator)
{
    public async Task<ArchitectureAnalysis> AnalyzeAsync(
        string productBrief,
        string featureText,
        FeatureSpecification approvedSpecification,
        string skillName,
        string skillPrompt,
        ArchitectureAnalysis? previousAnalysis,
        string? reviewerFeedback,
        int revision,
        RepositoryCatalog? repositoryCatalog,
        RepositorySourceReference repositorySource,
        CancellationToken cancellationToken)
    {
        var input = JsonSerializer.Serialize(new
        {
            productBrief,
            requestedFeature = featureText,
            approvedFeatureSpecification = approvedSpecification,
            skillName,
            skillPrompt,
            previousAnalysis,
            reviewerFeedback,
            revision,
            repositoryCatalog,
            repositorySource
        }, JsonSerializerOptions.Web);
        var analysis = await generator.AnalyzeArchitectureAsync(input, cancellationToken);
        InputRules.ValidateArchitectureAnalysis(
            analysis,
            repositorySource.Paths.ToHashSet(StringComparer.Ordinal));
        return analysis;
    }
}

/// <summary>Selects an exactly sized, role-matched review group for one feature.</summary>
public sealed class DeveloperReviewerSelector(IDeveloperReviewerSelector selector)
{
    public async Task<DeveloperReviewerMatch[]> SelectAsync(
        string featureText,
        FeatureSpecification specification,
        DeveloperConfigurationProfile[] developers,
        int reviewerCount,
        CancellationToken cancellationToken)
    {
        if (!InputRules.IsValidReviewerCount(reviewerCount) || developers.Length < reviewerCount)
        {
            throw new InvalidOperationException(
                $"The feature requests {reviewerCount} reviewers, but only {developers.Length} enabled role profiles are available.");
        }

        var input = JsonSerializer.Serialize(new
        {
            featureText,
            approvedFeatureSpecification = specification,
            reviewerCount,
            developers
        }, JsonSerializerOptions.Web);
        var matches = await selector.SelectAsync(input, cancellationToken);
        if (matches is not { Length: > 0 } || matches.Length != reviewerCount ||
            matches.Select(x => x.UserId).Distinct(StringComparer.Ordinal).Count() != reviewerCount)
        {
            throw new InvalidDataException("Reviewer selection must return exactly the requested number of distinct developers.");
        }

        var profiles = developers.ToDictionary(x => x.UserId, StringComparer.Ordinal);
        foreach (var match in matches)
        {
            if (!profiles.TryGetValue(match.UserId, out var profile) ||
                match.MatchedRoles is not { Length: > 0 } ||
                match.MatchedRoles.Any(role => !profile.Roles.Contains(role, StringComparer.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("Reviewer selection returned an unknown developer or an unconfigured role.");
            }
        }

        return matches;
    }
}

/// <summary>Generates structured feature specifications for the business-analysis gate.</summary>
public interface IFeatureSpecificationGenerator
{
    /// <summary>Generates a draft specification from serialized, untrusted request data.</summary>
    Task<FeatureSpecification> GenerateFeatureSpecificationAsync(string input, CancellationToken cancellationToken);
}

/// <summary>Generates a structured architecture assessment from approved requirements and repository evidence.</summary>
public interface IArchitectureAnalysisGenerator
{
    Task<ArchitectureAnalysis> AnalyzeArchitectureAsync(string input, CancellationToken cancellationToken);
}

/// <summary>Generates one structured analysis using one captured developer skill.</summary>
public interface IDeveloperSkillAnalysisGenerator
{
    Task<DeveloperSkillDraft> AnalyzeDeveloperSkillAsync(string input, CancellationToken cancellationToken);
}

/// <summary>Compares specialist outputs and returns source-linked agreements, conflicts, and open questions.</summary>
public interface IDeveloperCollaborationGenerator
{
    /// <summary>Returns structured collaboration findings from serialized, untrusted specialist results.</summary>
    Task<CollaborationSynthesis> SynthesizeDeveloperCollaborationAsync(
        string input, CancellationToken cancellationToken);
}

/// <summary>A provider of draft plans from serialized, untrusted request data.</summary>
public interface IPlanGenerator
{
    /// <summary>Generates a draft; provider and output errors propagate to the workflow.</summary>
    Task<DraftPlan> GenerateAsync(string input, CancellationToken cancellationToken);
}

/// <summary>Creates a structured functionality catalog from a pinned repository source.</summary>
public interface IRepositoryCatalogGenerator
{
    /// <summary>Analyzes the pinned repository through scoped tools; errors propagate to the workflow.</summary>
    Task<RepositoryFunctionality[]> GenerateCatalogAsync(string input, CancellationToken cancellationToken);
}

/// <summary>Implements a human-approved plan in the supplied checked-out worktree.</summary>
public interface ICodeChangeGenerator
{
    /// <summary>Returns an implementation summary; provider and output errors propagate to the workflow.</summary>
    Task<string> GenerateCodeChangeAsync(string input, CancellationToken cancellationToken);
}

/// <summary>Selects reviewer profiles and roles from serialized feature and profile data.</summary>
public interface IDeveloperReviewerSelector
{
    /// <summary>Returns exactly the requested number of distinct role-matched developers.</summary>
    Task<DeveloperReviewerMatch[]> SelectAsync(string input, CancellationToken cancellationToken);
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
                services.AddSingleton<IFeatureSpecificationGenerator, OfflineFeatureSpecificationGenerator>();
                services.AddSingleton<IArchitectureAnalysisGenerator, OfflineArchitectureAnalysisGenerator>();
                services.AddSingleton<IDeveloperSkillAnalysisGenerator, OfflineDeveloperSkillAnalysisGenerator>();
                services.AddSingleton<IDeveloperCollaborationGenerator, OfflineDeveloperCollaborationGenerator>();
                services.AddSingleton<IDeveloperReviewerSelector, OfflineDeveloperReviewerSelector>();
                services.AddSingleton<IRepositoryCatalogGenerator, OfflineRepositoryCatalogGenerator>();
                services.AddSingleton<ICodeChangeGenerator, OfflineCodeChangeGenerator>();
                break;
            case "Copilot":
                services.AddSingleton<CopilotPlanGenerator>();
                services.AddSingleton<IPlanGenerator>(provider => provider.GetRequiredService<CopilotPlanGenerator>());
                services.AddSingleton<IFeatureSpecificationGenerator>(provider =>
                    provider.GetRequiredService<CopilotPlanGenerator>());
                services.AddSingleton<IArchitectureAnalysisGenerator>(provider =>
                    provider.GetRequiredService<CopilotPlanGenerator>());
                services.AddSingleton<IDeveloperSkillAnalysisGenerator>(provider =>
                    provider.GetRequiredService<CopilotPlanGenerator>());
                services.AddSingleton<IDeveloperCollaborationGenerator>(provider =>
                    provider.GetRequiredService<CopilotPlanGenerator>());
                services.AddSingleton<IDeveloperReviewerSelector>(provider =>
                    provider.GetRequiredService<CopilotPlanGenerator>());
                services.AddSingleton<IRepositoryCatalogGenerator>(provider => provider.GetRequiredService<CopilotPlanGenerator>());
                services.AddSingleton<ICodeChangeGenerator>(provider => provider.GetRequiredService<CopilotPlanGenerator>());
                break;
            default:
                throw new InvalidOperationException("Agents:Mode must be Offline or Copilot.");
        }
        services.AddSingleton<Planner>();
        services.AddSingleton<BusinessAnalyst>();
        services.AddSingleton<Architect>();
        services.AddSingleton<DeveloperSpecialist>();
        services.AddSingleton<DeveloperCollaborator>();
        services.AddSingleton<DeveloperReviewerSelector>();
        return services;
    }
}

/// <summary>Clearly states that cross-role comparison was not performed in offline mode.</summary>
public sealed class OfflineDeveloperCollaborationGenerator : IDeveloperCollaborationGenerator
{
    /// <summary>Returns an explicit placeholder instead of pretending offline mode compared the roles.</summary>
    public Task<CollaborationSynthesis> SynthesizeDeveloperCollaborationAsync(
        string input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CollaborationSynthesis(
            "Offline demonstration only; role agreement and conflict analysis was not performed.",
            [],
            [],
            [new CollaborationQuestion(
                "Have a human compare the individual developer skill analyses before approving the plan.",
                [])]));
    }
}

/// <summary>Clearly labeled offline output; it does not claim role-specific repository analysis.</summary>
public sealed class OfflineDeveloperSkillAnalysisGenerator : IDeveloperSkillAnalysisGenerator
{
    public Task<DeveloperSkillDraft> AnalyzeDeveloperSkillAsync(
        string input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new DeveloperSkillDraft(
            "Offline demonstration only; no model-based specialist analysis was performed.",
            ["Review this role's implementation responsibilities against the approved feature and repository."],
            ["This offline result is not a repository-grounded technical assessment."],
            ["Have a qualified developer review the proposal and run the required checks."],
            []));
    }
}

/// <summary>Clearly labeled offline placeholder that makes no repository architecture claims.</summary>
public sealed class OfflineArchitectureAnalysisGenerator : IArchitectureAnalysisGenerator
{
    public Task<ArchitectureAnalysis> AnalyzeArchitectureAsync(
        string input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ArchitectureAnalysis(
            "Offline demonstration only; no model-based architecture analysis was performed.",
            [],
            [],
            ["Architecture evidence and options must be reviewed before planning."],
            ["Which existing components and constraints should guide this change?"],
            []));
    }
}

/// <summary>Deterministic role matching for explicit offline demonstration mode.</summary>
public sealed class OfflineDeveloperReviewerSelector : IDeveloperReviewerSelector
{
    public Task<DeveloperReviewerMatch[]> SelectAsync(string input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(input);
        var root = document.RootElement;
        var feature = string.Join(' ', new[]
        {
            root.GetProperty("featureText").GetString(),
            root.GetProperty("approvedFeatureSpecification").GetRawText()
        }).ToLowerInvariant();
        var count = root.GetProperty("reviewerCount").GetInt32();
        var profiles = root.GetProperty("developers").Deserialize<DeveloperConfigurationProfile[]>(
            JsonSerializerOptions.Web) ?? [];
        var ranked = profiles
            .SelectMany(profile => profile.Roles
                .Where(role => RoleMatchesFeature(role, feature))
                .Select(role => new DeveloperReviewerMatch(profile.UserId, [role])))
            .DistinctBy(match => match.UserId, StringComparer.Ordinal)
            .Take(count)
            .ToArray();
        var selected = ranked.ToList();
        foreach (var profile in profiles.Where(profile =>
            selected.All(match => match.UserId != profile.UserId)).Take(count - selected.Count))
        {
            selected.Add(new DeveloperReviewerMatch(profile.UserId, [profile.Roles[0]]));
        }
        var matches = selected.ToArray();
        return Task.FromResult(matches);
    }

    private static bool RoleMatchesFeature(string role, string feature)
    {
        var roleTerms = role.ToLowerInvariant().Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        var aliases = roleTerms.SelectMany(term => term switch
        {
            "architect" or "architecture" => new[] { "architect", "architecture", "design" },
            "frontend" or "front-end" or "ui" => new[] { "frontend", "front-end", "ui", "user interface" },
            "database" or "db" or "data" => new[] { "database", "db", "data", "sql", "storage" },
            "microservice" or "backend" or "back-end" => new[] { "microservice", "service", "backend", "back-end", "api" },
            _ => new[] { term }
        });
        return aliases.Any(feature.Contains);
    }
}

/// <summary>Deterministic draft for exercising the BA gate without claiming model analysis.</summary>
public sealed class OfflineFeatureSpecificationGenerator : IFeatureSpecificationGenerator
{
    public Task<FeatureSpecification> GenerateFeatureSpecificationAsync(
        string input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = JsonDocument.Parse(input);
        var root = document.RootElement;
        var request = root.GetProperty("requestedFeature").GetString() ?? string.Empty;
        var revision = root.GetProperty("revision").GetInt32();
        var previous = root.TryGetProperty("previousSpecification", out var prior) &&
            prior.ValueKind == JsonValueKind.Object
                ? prior.Deserialize<FeatureSpecification>(JsonSerializerOptions.Web)
                : null;
        var feedback = root.GetProperty("analystFeedback").GetString();
        var specification = previous is null
            ? new FeatureSpecification(
                "Offline draft: " + Truncate(request, 180),
                "Offline demonstration only; no model-based business analysis was performed. " +
                "A business analyst must validate this problem statement.",
                ["As a product user, I want the submitted feature request evaluated before implementation."],
                ["A business analyst reviews and approves the feature specification before developer planning."],
                [],
                [],
                ["What user outcome and measurable acceptance evidence should this feature require?"])
            : previous with
            {
                ProblemStatement =
                    $"Offline demonstration only (revision {revision}); feedback to address: {Truncate(feedback ?? string.Empty, 500)}"
            };
        return Task.FromResult(specification);
    }

    private static string Truncate(string value, int maximum) =>
        value.Length <= maximum ? value.Trim() : value[..maximum].Trim() + "...";
}

/// <summary>Rejects implementation generation in offline mode rather than inventing source changes.</summary>
public sealed class OfflineCodeChangeGenerator : ICodeChangeGenerator
{
    public Task<string> GenerateCodeChangeAsync(string input, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Code generation requires Agents:Mode=Copilot.");
}

/// <summary>Rejects repository analysis in deterministic offline mode rather than inventing source evidence.</summary>
public sealed class OfflineRepositoryCatalogGenerator : IRepositoryCatalogGenerator
{
    public Task<RepositoryFunctionality[]> GenerateCatalogAsync(string input, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Repository catalog generation requires Agents:Mode=Copilot.");
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
