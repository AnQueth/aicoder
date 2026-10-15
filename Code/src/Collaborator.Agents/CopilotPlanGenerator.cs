using Collaborator.Core;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Collaborator.Agents;

/// <summary>Runs structured GitHub Copilot sessions with scoped tools over supplied repository snapshots.</summary>
public sealed class CopilotPlanGenerator(IConfiguration config, ILogger<CopilotPlanGenerator> logger)
    : IPlanGenerator, IFeatureSpecificationGenerator, IDeveloperReviewerSelector,
        IRepositoryCatalogGenerator, ICodeChangeGenerator, IArchitectureAnalysisGenerator,
        IDeveloperSkillAnalysisGenerator, IDeveloperCollaborationGenerator
{
    private static readonly JsonElement ReviewerSelectionSchema = JsonSerializer.Deserialize<JsonElement>("""
        {
          "type": "object",
          "properties": {
            "reviewers": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "userId": { "type": "string" },
                  "matchedRoles": { "type": "array", "items": { "type": "string" } }
                },
                "required": ["userId", "matchedRoles"],
                "additionalProperties": false
              }
            }
          },
          "required": ["reviewers"],
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement FeatureSpecificationSchema = JsonSerializer.Deserialize<JsonElement>("""
        {
          "type": "object",
          "properties": {
              "title": { "type": "string", "description": "Required nonempty title of at most 200 characters." },
              "problemStatement": { "type": "string", "description": "Required nonempty problem and desired outcome, at most 4000 characters." },
              "userStories": { "type": "array", "description": "1 to 30 nonempty outcome-oriented stories, each at most 1000 characters.", "items": { "type": "string" } },
              "acceptanceCriteria": { "type": "array", "description": "1 to 50 nonempty observable, testable criteria, each at most 1000 characters.", "items": { "type": "string" } },
              "constraints": { "type": "array", "description": "0 to 30 nonempty known constraints, each at most 1000 characters. Use an empty array if none are known.", "items": { "type": "string" } },
              "outOfScope": { "type": "array", "description": "0 to 30 nonempty explicit exclusions, each at most 1000 characters. Use an empty array if none are known.", "items": { "type": "string" } },
              "openQuestions": { "type": "array", "description": "0 to 30 nonempty unresolved questions, each at most 1000 characters. Use an empty array if none remain.", "items": { "type": "string" } }
          },
          "required": [
            "title", "problemStatement", "userStories", "acceptanceCriteria",
            "constraints", "outOfScope", "openQuestions"
          ],
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement PlanSchema = JsonSerializer.Deserialize<JsonElement>("""
        {
          "type": "object",
          "properties": {
            "summary": {
              "type": "string",
              "description": "A nonempty description of the requested outcome, at most 16000 characters."
            },
            "steps": {
              "type": "array",
              "description": "Required: between 1 and 50 nonempty implementation steps, each at most 16000 characters.",
              "items": { "type": "string" }
            },
            "openQuestions": {
              "type": "array",
              "description": "Required: 0 to 50 nonempty questions, each at most 16000 characters. Use an empty array, never null, if none remain.",
              "items": { "type": "string" }
            }
          },
          "required": ["summary", "steps", "openQuestions"],
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement ArchitectureAnalysisSchema = JsonSerializer.Deserialize<JsonElement>("""
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string" },
            "componentImpacts": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "component": { "type": "string" },
                  "impact": { "type": "string" },
                  "evidence": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "properties": {
                        "path": { "type": "string" },
                        "symbol": { "type": ["string", "null"] },
                        "startLine": { "type": ["integer", "null"] },
                        "endLine": { "type": ["integer", "null"] },
                        "claim": { "type": "string" }
                      },
                      "required": ["path", "symbol", "startLine", "endLine", "claim"],
                      "additionalProperties": false
                    }
                  }
                },
                "required": ["component", "impact", "evidence"],
                "additionalProperties": false
              }
            },
            "options": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "name": { "type": "string" },
                  "description": { "type": "string" },
                  "benefits": { "type": "array", "items": { "type": "string" } },
                  "tradeoffs": { "type": "array", "items": { "type": "string" } }
                },
                "required": ["name", "description", "benefits", "tradeoffs"],
                "additionalProperties": false
              }
            },
            "risks": { "type": "array", "items": { "type": "string" } },
            "openQuestions": { "type": "array", "items": { "type": "string" } },
            "adrProposals": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "title": { "type": "string" },
                  "context": { "type": "string" },
                  "options": { "type": "array", "items": { "type": "string" } },
                  "recommendation": { "type": "string" },
                  "rationale": { "type": "string" }
                },
                "required": ["title", "context", "options", "recommendation", "rationale"],
                "additionalProperties": false
              }
            }
          },
          "required": ["summary", "componentImpacts", "options", "risks", "openQuestions", "adrProposals"],
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement DeveloperSkillAnalysisSchema = JsonSerializer.Deserialize<JsonElement>("""
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string" },
            "proposedChanges": { "type": "array", "items": { "type": "string" } },
            "risks": { "type": "array", "items": { "type": "string" } },
            "verificationNeeds": { "type": "array", "items": { "type": "string" } },
            "openQuestions": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["summary", "proposedChanges", "risks", "verificationNeeds", "openQuestions"],
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement DeveloperCollaborationSchema = JsonSerializer.Deserialize<JsonElement>("""
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string" },
            "agreements": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "finding": { "type": "string" },
                  "sources": {
                    "type": "array",
                    "items": { "$ref": "#/$defs/skill" }
                  }
                },
                "required": ["finding", "sources"],
                "additionalProperties": false
              }
            },
            "conflicts": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "finding": { "type": "string" },
                  "sources": {
                    "type": "array",
                    "items": { "$ref": "#/$defs/skill" }
                  }
                },
                "required": ["finding", "sources"],
                "additionalProperties": false
              }
            },
            "unresolvedQuestions": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "question": { "type": "string" },
                  "sources": {
                    "type": "array",
                    "items": { "$ref": "#/$defs/skill" }
                  }
                },
                "required": ["question", "sources"],
                "additionalProperties": false
              }
            }
          },
          "$defs": {
            "skill": {
              "type": "object",
              "properties": {
                "roleId": { "type": "string" },
                "skillId": { "type": "string" },
                "domain": { "type": "string" },
                "revision": { "type": "integer" }
              },
              "required": ["roleId", "skillId", "domain", "revision"],
              "additionalProperties": false
            }
          },
          "required": ["summary", "agreements", "conflicts", "unresolvedQuestions"],
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement RepositoryCatalogSchema = JsonSerializer.Deserialize<JsonElement>("""
        {
          "type": "object",
          "properties": {
            "functionalities": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "name": { "type": "string" },
                  "description": { "type": "string" },
                  "paths": { "type": "array", "items": { "type": "string" } },
                  "symbols": { "type": "array", "items": { "type": "string" } }
                },
                "required": ["name", "description", "paths", "symbols"],
                "additionalProperties": false
              }
            }
          },
          "required": ["functionalities"],
          "additionalProperties": false
        }
        """);

    private static readonly JsonElement CodeChangeSummarySchema = JsonSerializer.Deserialize<JsonElement>("""
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string" }
          },
          "required": ["summary"],
          "additionalProperties": false
        }
        """);

    public async Task<FeatureSpecification> GenerateFeatureSpecificationAsync(
        string input, CancellationToken cancellationToken)
    {
        var definition = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Definitions", "business-analyst.md"), cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        await using var client = new CopilotClient();
        await client.StartAsync(deadline.Token);
        var repositoryContext = RepositorySourceTools.PrepareInput(input);
        await using var session = await client.CreateSessionAsync(new SessionConfig
        {
            Model = config["Agents:Copilot:Model"],
            AvailableTools = [],
            Tools = repositoryContext.Tools,
            EnableConfigDiscovery = false,
            SkipEmbeddingRetrieval = true,
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = definition
            },
#pragma warning disable GHCP001 // The pinned SDK exposes permission decisions as experimental.
            OnPermissionRequest = (_, _) =>
                Task.FromResult(PermissionDecision.Reject("Only registered read-only repository tools may execute."))
#pragma warning restore GHCP001
        }, deadline.Token);
        using var toolInvocationSubscription = ObserveRepositoryToolInvocations(session);
        logger.LogInformation(
            "Copilot business-analysis session started with {ToolCount} repository tools.",
            repositoryContext.Tools.Count);
        try
        {
            var prompt = repositoryContext.Prompt;
            for (var attempt = 1; attempt <= 2; attempt++)
            {
#pragma warning disable GHCP001 // Structured output is experimental in SDK 1.0.16.
                var response = await session.SendAndWaitAsync(new MessageOptions
                {
                    Prompt = prompt,
                    ResponseSchema = FeatureSpecificationSchema
                }, timeout: TimeSpan.FromMinutes(3), cancellationToken: deadline.Token);
#pragma warning restore GHCP001
                try
                {
                    var specification = JsonSerializer.Deserialize<FeatureSpecification>(
                        response?.Data.Content ?? throw new InvalidDataException("Copilot returned no feature specification."),
                        JsonSerializerOptions.Web)
                        ?? throw new InvalidDataException("Copilot returned a null feature specification.");
                    InputRules.ValidateFeatureSpecification(specification);
                    logger.LogInformation(
                        "Copilot business-analysis session completed for specification {TitleLength}.",
                        specification.Title.Length);
                    return specification;
                }
                catch (Exception ex) when (attempt == 1 && (ex is InvalidDataException || ex is JsonException))
                {
                    logger.LogWarning(
                        "Copilot business-analysis output failed validation; requesting one corrected response: {Reason}",
                        ex.Message);
                    prompt = BuildValidationRepairPrompt(ex.Message);
                }
            }
            throw new InvalidOperationException("Business-analysis retry completed without a valid specification.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            logger.LogWarning("Copilot business analysis was cancelled or timed out; aborting the session.");
            try
            {
                await session.AbortAsync(CancellationToken.None);
            }
            catch (Exception abortError)
            {
                logger.LogError(abortError, "Copilot session abort failed after cancellation or timeout.");
            }
            throw;
        }
    }

    public async Task<ArchitectureAnalysis> AnalyzeArchitectureAsync(
        string input, CancellationToken cancellationToken)
    {
        var definition = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Definitions", "architect.md"), cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        await using var client = new CopilotClient();
        await client.StartAsync(deadline.Token);
        var repositoryContext = RepositorySourceTools.PrepareInput(input);
        var inspectedPaths = GetInspectedPaths(input);
        await using var session = await client.CreateSessionAsync(new SessionConfig
        {
            Model = config["Agents:Copilot:Model"],
            AvailableTools = [],
            Tools = repositoryContext.Tools,
            EnableConfigDiscovery = false,
            SkipEmbeddingRetrieval = true,
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = definition
            },
#pragma warning disable GHCP001 // The pinned SDK exposes permission decisions as experimental.
            OnPermissionRequest = (_, _) =>
                Task.FromResult(PermissionDecision.Reject("Only registered read-only repository tools may execute."))
#pragma warning restore GHCP001
        }, deadline.Token);
        using var toolInvocationSubscription = ObserveRepositoryToolInvocations(session);
        try
        {
            var prompt = repositoryContext.Prompt;
            for (var attempt = 1; attempt <= 2; attempt++)
            {
#pragma warning disable GHCP001 // Structured output is experimental in SDK 1.0.16.
                var response = await session.SendAndWaitAsync(new MessageOptions
                {
                    Prompt = prompt,
                    ResponseSchema = ArchitectureAnalysisSchema
                }, timeout: TimeSpan.FromMinutes(3), cancellationToken: deadline.Token);
#pragma warning restore GHCP001
                try
                {
                    var analysis = JsonSerializer.Deserialize<ArchitectureAnalysis>(
                        response?.Data.Content ?? throw new InvalidDataException("Copilot returned no architecture analysis."),
                        JsonSerializerOptions.Web)
                        ?? throw new InvalidDataException("Copilot returned a null architecture analysis.");
                    InputRules.ValidateArchitectureAnalysis(analysis, inspectedPaths);
                    return analysis;
                }
                catch (Exception ex) when (attempt == 1 && (ex is InvalidDataException || ex is JsonException))
                {
                    logger.LogWarning(
                        "Copilot architecture output failed validation; requesting one corrected response: {Reason}",
                        ex.Message);
                    prompt = BuildValidationRepairPrompt(ex.Message);
                }
            }
            throw new InvalidOperationException("Architecture analysis retry completed without valid evidence.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            logger.LogWarning("Copilot architecture analysis was cancelled or timed out; aborting the session.");
            try
            {
                await session.AbortAsync(CancellationToken.None);
            }
            catch (Exception abortError)
            {
                logger.LogError(abortError, "Copilot session abort failed after cancellation or timeout.");
            }
            throw;
        }
    }

    public async Task<DeveloperSkillDraft> AnalyzeDeveloperSkillAsync(
        string input, CancellationToken cancellationToken)
    {
        var definition = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Definitions", "developer-specialist.md"), cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        await using var client = new CopilotClient();
        await client.StartAsync(deadline.Token);
        var repositoryContext = RepositorySourceTools.PrepareInput(input);
        await using var session = await client.CreateSessionAsync(new SessionConfig
        {
            Model = config["Agents:Copilot:Model"],
            AvailableTools = [],
            Tools = repositoryContext.Tools,
            EnableConfigDiscovery = false,
            SkipEmbeddingRetrieval = true,
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = definition
            },
#pragma warning disable GHCP001 // The pinned SDK exposes permission decisions as experimental.
            OnPermissionRequest = (_, _) =>
                Task.FromResult(PermissionDecision.Reject("Only registered read-only repository tools may execute."))
#pragma warning restore GHCP001
        }, deadline.Token);
        using var toolInvocationSubscription = ObserveRepositoryToolInvocations(session);
        try
        {
#pragma warning disable GHCP001 // Structured output is experimental in SDK 1.0.16.
            var prompt = repositoryContext.Prompt;
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var response = await session.SendAndWaitAsync(new MessageOptions
                {
                    Prompt = prompt,
                    ResponseSchema = DeveloperSkillAnalysisSchema
                }, timeout: TimeSpan.FromMinutes(3), cancellationToken: deadline.Token);
#pragma warning restore GHCP001
                try
                {
                    var draft = JsonSerializer.Deserialize<DeveloperSkillDraft>(
                        response?.Data.Content ?? throw new InvalidDataException("Copilot returned no developer skill analysis."),
                        JsonSerializerOptions.Web)
                        ?? throw new InvalidDataException("Copilot returned a null developer skill analysis.");
                    InputRules.ValidateDeveloperSkillDraft(draft);
                    return draft;
                }
                catch (Exception ex) when (attempt == 1 && (ex is InvalidDataException || ex is JsonException))
                {
                    logger.LogWarning(
                        "Copilot developer analysis failed validation; requesting one corrected response: {Reason}",
                        ex.Message);
                    prompt = BuildValidationRepairPrompt(ex.Message);
                }
            }
            throw new InvalidOperationException("Developer analysis retry completed without a valid draft.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            logger.LogWarning("Copilot developer analysis was cancelled or timed out; aborting the session.");
            try
            {
                await session.AbortAsync(CancellationToken.None);
            }
            catch (Exception abortError)
            {
                logger.LogError(abortError, "Copilot session abort failed after cancellation or timeout.");
            }
            throw;
        }
    }

    /// <summary>Compares specialist results with optional read-only access to the pinned repository.</summary>
    public async Task<CollaborationSynthesis> SynthesizeDeveloperCollaborationAsync(
        string input, CancellationToken cancellationToken)
    {
        using var inputDocument = JsonDocument.Parse(input);
        var capturedSkills = inputDocument.RootElement.GetProperty("capturedSkills")
            .Deserialize<AgentSkillRevisionReference[]>(JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("Collaboration input must contain captured skill revisions.");
        var definition = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Definitions", "developer-collaboration.md"), cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        await using var client = new CopilotClient();
        await client.StartAsync(deadline.Token);
        var repositoryContext = RepositorySourceTools.PrepareInput(input);
        await using var session = await client.CreateSessionAsync(new SessionConfig
        {
            Model = config["Agents:Copilot:Model"],
            AvailableTools = [],
            Tools = repositoryContext.Tools,
            EnableConfigDiscovery = false,
            SkipEmbeddingRetrieval = true,
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = definition
            },
#pragma warning disable GHCP001 // The pinned SDK exposes permission decisions as experimental.
            OnPermissionRequest = (_, _) =>
                Task.FromResult(PermissionDecision.Reject("Only registered read-only repository tools may execute."))
#pragma warning restore GHCP001
        }, deadline.Token);
        using var toolInvocationSubscription = ObserveRepositoryToolInvocations(session);

        try
        {
#pragma warning disable GHCP001 // Structured output is experimental in SDK 1.0.16.
            var prompt = repositoryContext.Prompt;
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var response = await session.SendAndWaitAsync(new MessageOptions
                {
                    Prompt = prompt,
                    ResponseSchema = DeveloperCollaborationSchema
                }, timeout: TimeSpan.FromMinutes(3), cancellationToken: deadline.Token);
#pragma warning restore GHCP001
                try
                {
                    var synthesis = JsonSerializer.Deserialize<CollaborationSynthesis>(
                        response?.Data.Content ?? throw new InvalidDataException("Copilot returned no collaboration synthesis."),
                        JsonSerializerOptions.Web)
                        ?? throw new InvalidDataException("Copilot returned a null collaboration synthesis.");
                    InputRules.ValidateCollaborationSynthesis(synthesis, capturedSkills.ToHashSet());
                    return synthesis;
                }
                catch (Exception ex) when (attempt == 1 && (ex is InvalidDataException || ex is JsonException))
                {
                    logger.LogWarning(
                        "Copilot collaboration synthesis failed validation; requesting one corrected response: {Reason}",
                        ex.Message);
                    prompt = BuildValidationRepairPrompt(ex.Message);
                }
            }
            throw new InvalidOperationException("Collaboration synthesis retry completed without a valid artifact.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            logger.LogWarning("Copilot collaboration synthesis was cancelled or timed out; aborting the session.");
            try
            {
                await session.AbortAsync(CancellationToken.None);
            }
            catch (Exception abortError)
            {
                logger.LogError(abortError, "Copilot session abort failed after collaboration synthesis cancellation or timeout.");
            }
            throw;
        }
    }

    public async Task<DraftPlan> GenerateAsync(string input, CancellationToken cancellationToken)
    {
        var definition = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Definitions", "planner.md"), cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        await using var client = new CopilotClient();
        await client.StartAsync(deadline.Token);
        var repositoryContext = RepositorySourceTools.PrepareInput(input);
        await using var session = await client.CreateSessionAsync(new SessionConfig
        {
            Model = config["Agents:Copilot:Model"],
            AvailableTools = [],
            Tools = repositoryContext.Tools,
            EnableConfigDiscovery = false,
            SkipEmbeddingRetrieval = true,
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = definition
            },
#pragma warning disable GHCP001 // The pinned SDK exposes permission decisions as experimental.
            OnPermissionRequest = (_, _) =>
                Task.FromResult(PermissionDecision.Reject("Only registered read-only repository tools may execute."))
#pragma warning restore GHCP001
        }, deadline.Token);
        using var toolInvocationSubscription = ObserveRepositoryToolInvocations(session);
        logger.LogInformation(
            "Copilot planning session started with {ToolCount} repository tools.",
            repositoryContext.Tools.Count);
        try
        {
            var prompt = repositoryContext.Prompt;
            for (var attempt = 1; attempt <= 2; attempt++)
            {
#pragma warning disable GHCP001 // Structured output is experimental in SDK 1.0.16.
                var response = await session.SendAndWaitAsync(new MessageOptions
                {
                    Prompt = prompt,
                    ResponseSchema = PlanSchema
                }, timeout: TimeSpan.FromMinutes(3), cancellationToken: deadline.Token);
#pragma warning restore GHCP001
                try
                {
                    var plan = JsonSerializer.Deserialize<DraftPlan>(
                        response?.Data.Content ?? throw new InvalidDataException("Copilot returned no draft plan."),
                        JsonSerializerOptions.Web)
                        ?? throw new InvalidDataException("Copilot returned a null draft plan.");
                    InputRules.ValidatePlan(plan);
                    logger.LogInformation(
                        "Copilot planning session completed: summary length {SummaryLength}, steps {StepCount}, questions {QuestionCount}.",
                        plan.Summary?.Length, plan.Steps?.Length, plan.OpenQuestions?.Length);
                    return plan;
                }
                catch (Exception ex) when (attempt == 1 && (ex is InvalidDataException || ex is JsonException))
                {
                    logger.LogWarning(
                        "Copilot planning output failed validation; requesting one corrected response: {Reason}",
                        ex.Message);
                    prompt = BuildValidationRepairPrompt(ex.Message);
                }
            }
            throw new InvalidOperationException("Plan generation retry completed without a valid plan.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            logger.LogWarning("Copilot planning was cancelled or timed out; aborting the session.");
            try
            {
                await session.AbortAsync(CancellationToken.None);
            }
            catch (Exception abortError)
            {
                logger.LogError(abortError, "Copilot session abort failed after cancellation or timeout.");
            }
            throw;
        }
    }

    public async Task<DeveloperReviewerMatch[]> SelectAsync(string input, CancellationToken cancellationToken)
    {
        var definition = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Definitions", "reviewer-selector.md"), cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        await using var client = new CopilotClient();
        await client.StartAsync(deadline.Token);
        await using var session = await client.CreateSessionAsync(new SessionConfig
        {
            Model = config["Agents:Copilot:Model"],
            AvailableTools = [],
            EnableConfigDiscovery = false,
            SkipEmbeddingRetrieval = true,
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = definition
            },
#pragma warning disable GHCP001 // The pinned SDK exposes permission decisions as experimental.
            OnPermissionRequest = (_, _) =>
                Task.FromResult(PermissionDecision.Reject("Reviewer selection does not need tools or permissions."))
#pragma warning restore GHCP001
        }, deadline.Token);

        try
        {
#pragma warning disable GHCP001 // Structured output is experimental in SDK 1.0.16.
            var response = await session.SendAndWaitAsync(new MessageOptions
            {
                Prompt = input,
                ResponseSchema = ReviewerSelectionSchema
            }, timeout: TimeSpan.FromMinutes(3), cancellationToken: deadline.Token);
#pragma warning restore GHCP001
            using var document = JsonDocument.Parse(
                response?.Data.Content ?? throw new InvalidDataException("Copilot returned no reviewer selection."));
            return document.RootElement.GetProperty("reviewers")
                .Deserialize<DeveloperReviewerMatch[]>(JsonSerializerOptions.Web)
                ?? throw new InvalidDataException("Copilot returned a null reviewer selection.");
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            logger.LogWarning("Copilot reviewer selection was cancelled or timed out; aborting the session.");
            try
            {
                await session.AbortAsync(CancellationToken.None);
            }
            catch (Exception abortError)
            {
                logger.LogError(abortError, "Copilot session abort failed after reviewer selection cancellation or timeout.");
            }
            throw;
        }
    }

    public async Task<RepositoryFunctionality[]> GenerateCatalogAsync(
        string input, CancellationToken cancellationToken)
    {
        var definition = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Definitions", "cataloger.md"), cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        await using var client = new CopilotClient();
        await client.StartAsync(deadline.Token);
        var repositoryContext = RepositorySourceTools.PrepareInput(input);
        await using var session = await client.CreateSessionAsync(new SessionConfig
        {
            Model = config["Agents:Copilot:Model"],
            AvailableTools = [],
            Tools = repositoryContext.Tools,
            EnableConfigDiscovery = false,
            SkipEmbeddingRetrieval = true,
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = definition
            },
#pragma warning disable GHCP001 // The pinned SDK exposes permission decisions as experimental.
            OnPermissionRequest = (_, _) =>
                Task.FromResult(PermissionDecision.Reject("Only registered read-only repository tools may execute."))
#pragma warning restore GHCP001
        }, deadline.Token);
        using var toolInvocationSubscription = ObserveRepositoryToolInvocations(session);
        logger.LogInformation(
            "Copilot repository-catalog session started with {ToolCount} repository tools.",
            repositoryContext.Tools.Count);
        try
        {
#pragma warning disable GHCP001 // Structured output is experimental in SDK 1.0.16.
            var response = await session.SendAndWaitAsync(new MessageOptions
            {
                Prompt = repositoryContext.Prompt,
                ResponseSchema = RepositoryCatalogSchema
            }, timeout: TimeSpan.FromMinutes(3), cancellationToken: deadline.Token);
#pragma warning restore GHCP001
            using var document = JsonDocument.Parse(
                response?.Data.Content ?? throw new InvalidDataException("Copilot returned no repository catalog."));
            var functionalities = document.RootElement.GetProperty("functionalities")
                .Deserialize<RepositoryFunctionality[]>(JsonSerializerOptions.Web)
                ?? throw new InvalidDataException("Copilot returned a null repository catalog.");
            logger.LogInformation(
                "Copilot repository-catalog session completed with {FunctionalityCount} entries.",
                functionalities.Length);
            return functionalities;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            logger.LogWarning("Copilot repository analysis was cancelled or timed out; aborting the session.");
            try
            {
                await session.AbortAsync(CancellationToken.None);
            }
            catch (Exception abortError)
            {
                logger.LogError(abortError, "Copilot session abort failed after cancellation or timeout.");
            }
            throw;
        }
    }

    public async Task<string> GenerateCodeChangeAsync(
        string input, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromMinutes(10);
        var definition = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Definitions", "implementer.md"), cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        await using var client = new CopilotClient();
        await client.StartAsync(deadline.Token);
        var repositoryContext = RepositorySourceTools.PrepareInput(input);
        if (repositoryContext.WorkingDirectory is not { Length: > 0 } workingDirectory ||
            !Directory.Exists(workingDirectory))
        {
            throw new InvalidDataException("Code generation requires an existing feature worktree.");
        }
        await using var session = await client.CreateSessionAsync(new SessionConfig
        {
            Model = config["Agents:Copilot:Model"],
            AvailableTools = new ToolSet().AddBuiltIn("*").AddCustom("*"),
            Tools = repositoryContext.Tools,
            WorkingDirectory = workingDirectory,
            EnableConfigDiscovery = false,
            SkipEmbeddingRetrieval = true,
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = definition
            },
#pragma warning disable GHCP001 // The pinned SDK exposes permission decisions as experimental.
            OnPermissionRequest = (_, _) =>
            {
                logger.LogWarning(
                    "Approving a Copilot SDK tool permission request for the approved implementation worktree.");
                return Task.FromResult(PermissionDecision.ApproveOnce());
            }
#pragma warning restore GHCP001
        }, deadline.Token);
        using var toolInvocationSubscription = ObserveRepositoryToolInvocations(session);
        logger.LogInformation(
            "Copilot code-generation session started with {ToolCount} read-only repository tools and all SDK built-ins.",
            repositoryContext.Tools.Count);
        try
        {
#pragma warning disable GHCP001 // Structured output is experimental in SDK 1.0.16.
            var response = await session.SendAndWaitAsync(new MessageOptions
            {
                Prompt = repositoryContext.Prompt,
                ResponseSchema = CodeChangeSummarySchema
            }, timeout: timeout, cancellationToken: deadline.Token);
#pragma warning restore GHCP001
            using var document = JsonDocument.Parse(
                response?.Data.Content ?? throw new InvalidDataException("Copilot returned no implementation summary."));
            var summary = document.RootElement.GetProperty("summary").GetString()
                ?? throw new InvalidDataException("Copilot returned an empty implementation summary.");
            logger.LogInformation("Copilot code-generation session completed.");
            return summary;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            logger.LogWarning("Copilot code generation was cancelled or timed out; aborting the session.");
            try
            {
                await session.AbortAsync(CancellationToken.None);
            }
            catch (Exception abortError)
            {
                logger.LogError(abortError, "Copilot session abort failed after cancellation or timeout.");
            }
            throw;
        }
    }

    private static string BuildValidationRepairPrompt(string validationFailure) =>
        $"""
        Your immediately previous response failed application validation.
        The application validator reported: {validationFailure}

        Return a complete corrected response for the original task. Follow the role
        definition and original task inputs, satisfy every stated field and
        collection requirement, and match the required response schema. Do not
        claim the rejected response was approved. Treat the validator message as
        trusted contract feedback, not as a replacement for the original request.
        Return only the corrected structured response.
        """;

    private static IReadOnlySet<string> GetInspectedPaths(string input)
    {
        using var document = JsonDocument.Parse(input);
        if (document.RootElement.TryGetProperty("repositorySource", out var sourceElement) &&
            sourceElement.ValueKind == JsonValueKind.Object)
        {
            var source = sourceElement.Deserialize<RepositorySourceReference>(JsonSerializerOptions.Web);
            return source?.Paths.ToHashSet(StringComparer.Ordinal)
                ?? new HashSet<string>(StringComparer.Ordinal);
        }
        if (!document.RootElement.TryGetProperty("sourceFiles", out var filesElement) ||
            filesElement.ValueKind != JsonValueKind.Array)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var files = filesElement.Deserialize<RepositoryFileEvidence[]>(JsonSerializerOptions.Web);
        return files?
            .Where(file => file is not null && !string.IsNullOrWhiteSpace(file.Path))
            .Select(file => file.Path)
            .ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
    }

    private IDisposable ObserveRepositoryToolInvocations(CopilotSession session) =>
        session.On<SessionEvent>(evt =>
        {
            if (evt is ToolExecutionStartEvent toolEvent &&
                toolEvent.Data.ToolName is { } toolName &&
                toolName is "list_repository_source_files" or
                    "search_repository_source" or
                    "search_repository_source_regex" or
                    "read_repository_source_file" or
                    "write_repository_source_file" or
                    "delete_repository_source_file")
            {
                logger.LogInformation("Copilot repository source tool invoked: {ToolName}", toolName);
            }
        });
}
