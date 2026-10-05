using Collaborator.Core;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Collaborator.Agents;

/// <summary>Runs tool-free, bounded planning sessions using the GitHub Copilot SDK.</summary>
public sealed class CopilotPlanGenerator(IConfiguration config, ILogger<CopilotPlanGenerator> logger)
    : IPlanGenerator, IRepositoryCatalogGenerator, ICodeChangeGenerator
{
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
              "description": "Between 1 and 50 nonempty implementation steps, each at most 16000 characters.",
              "items": { "type": "string" }
            },
            "openQuestions": {
              "type": "array",
              "description": "Between 0 and 50 nonempty questions, each at most 16000 characters. Use an empty array if none.",
              "items": { "type": "string" }
            }
          },
          "required": ["summary", "steps", "openQuestions"],
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

    private static readonly JsonElement CodeChangeSchema = JsonSerializer.Deserialize<JsonElement>("""
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string" },
            "files": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "path": { "type": "string" },
                  "operation": { "type": "string", "enum": ["add", "update", "delete"] },
                  "content": {
                    "anyOf": [
                      { "type": "string" },
                      { "type": "null" }
                    ]
                  }
                },
                "required": ["path", "operation", "content"],
                "additionalProperties": false
              }
            }
          },
          "required": ["summary", "files"],
          "additionalProperties": false
        }
        """);

    public async Task<DraftPlan> GenerateAsync(string input, CancellationToken cancellationToken)
    {
        var definition = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Definitions", "planner.md"), cancellationToken);
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
                Task.FromResult(PermissionDecision.Reject("Planning does not permit tool execution."))
#pragma warning restore GHCP001
        }, deadline.Token);
        logger.LogInformation("Copilot planning session started; tools are disabled.");
        try
        {
#pragma warning disable GHCP001 // Structured output is experimental in SDK 1.0.16.
            var response = await session.SendAndWaitAsync(new MessageOptions
            {
                Prompt = input,
                ResponseSchema = PlanSchema
            }, timeout: TimeSpan.FromMinutes(3), cancellationToken: deadline.Token);
#pragma warning restore GHCP001
            var plan = JsonSerializer.Deserialize<DraftPlan>(
                response?.Data.Content ?? throw new InvalidDataException("Copilot returned no draft plan."),
                JsonSerializerOptions.Web)
                ?? throw new InvalidDataException("Copilot returned a null draft plan.");
            logger.LogInformation(
                "Copilot planning session completed: summary length {SummaryLength}, steps {StepCount}, questions {QuestionCount}.",
                plan.Summary?.Length, plan.Steps?.Length, plan.OpenQuestions?.Length);
            return plan;
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

    public async Task<RepositoryFunctionality[]> GenerateCatalogAsync(
        string input, CancellationToken cancellationToken)
    {
        var definition = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Definitions", "cataloger.md"), cancellationToken);
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
                Task.FromResult(PermissionDecision.Reject("Repository analysis does not permit tool execution."))
#pragma warning restore GHCP001
        }, deadline.Token);
        logger.LogInformation("Copilot repository-catalog session started; tools are disabled.");
        try
        {
#pragma warning disable GHCP001 // Structured output is experimental in SDK 1.0.16.
            var response = await session.SendAndWaitAsync(new MessageOptions
            {
                Prompt = input,
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

    public async Task<CodeChangeProposal> GenerateCodeChangeAsync(
        string input, CancellationToken cancellationToken)
    {
        var definition = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Definitions", "implementer.md"), cancellationToken);
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
                Task.FromResult(PermissionDecision.Reject("Code generation does not permit tool execution."))
#pragma warning restore GHCP001
        }, deadline.Token);
        logger.LogInformation("Copilot code-generation session started; tools are disabled.");
        try
        {
#pragma warning disable GHCP001 // Structured output is experimental in SDK 1.0.16.
            var response = await session.SendAndWaitAsync(new MessageOptions
            {
                Prompt = input,
                ResponseSchema = CodeChangeSchema
            }, timeout: TimeSpan.FromMinutes(3), cancellationToken: deadline.Token);
#pragma warning restore GHCP001
            var proposal = JsonSerializer.Deserialize<CodeChangeProposal>(
                response?.Data.Content ?? throw new InvalidDataException("Copilot returned no implementation patch."),
                JsonSerializerOptions.Web)
                ?? throw new InvalidDataException("Copilot returned a null implementation patch.");
            logger.LogInformation(
                "Copilot code-generation session completed with {FileCount} proposed files.",
                proposal.Files?.Length);
            return proposal;
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
}
