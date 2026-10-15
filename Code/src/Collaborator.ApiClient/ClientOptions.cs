using System.Text.Json;
using Collaborator.Core;

namespace Collaborator.ApiClient;

/// <summary>Command-line options for the end-to-end client. Parsed by hand to keep the tool dependency-free.</summary>
internal sealed class ClientOptions
{
    public const string AppHostSecretsId = "6f772ef0-127a-4100-b2d6-70cfaacbb414";
    public const string ApiSecretsId = "collaborator-api-local";

    public Uri BaseUrl { get; private set; } = new("http://localhost:5076");
    public string? ApiKey { get; private set; }
    public string ProductId { get; private set; } = "demo-product";
    public string ProductName { get; private set; } = "Demo Product";
    public string ProductBrief { get; private set; } =
        "A small web application used to exercise the Collaborator feature-request flow end to end.";
    public string? ProductRepositoryUrl { get; private set; }
    public string? RepositoryBaseBranch { get; private set; }
    public string? Feature { get; private set; }
    public string BusinessAnalystId { get; private set; } = "default";
    public string BusinessAnalystSkillId { get; private set; } = "requirements-analysis";
    public string? BusinessAnalystSkillPrompt { get; private set; }
    public string? ArchitectureSkillPrompt { get; private set; }
    public int ReviewerCount { get; private set; } = 3;
    public Guid? ExistingRunId { get; private set; }
    public List<DeveloperOption> Developers { get; } = [];
    public List<DeveloperSkillSelection>? DeveloperSkills { get; private set; }
    public Dictionary<string, string> ReviewResponses { get; } = new(StringComparer.Ordinal);
    public List<string> Feedback { get; } = [];
    public string? FinalDecision { get; private set; }
    public string? RejectReason { get; private set; }
    public bool Interactive { get; private set; }
    public bool Quiet { get; private set; }
    public bool Verbose { get; private set; }
    public TimeSpan Timeout { get; private set; } = TimeSpan.FromMinutes(5);
    public TimeSpan PollInterval { get; private set; } = TimeSpan.FromSeconds(1);
    public bool ShowHelp { get; private set; }

    public static string Usage => """
        Collaborator API client - drives the full feature-request flow against a running API.

        Usage:
          dotnet run --project src/Collaborator.ApiClient -- [options]

        Flow: health -> configure skills and developers -> register product -> submit feature ->
              BA clarification and approval -> architecture review -> cross-role collaboration ->
              developer plan review and approval -> summary.

        Connection:
          --base-url <url>        API base URL (default http://localhost:5076; env COLLABORATOR_API_URL)
          --api-key <key>         API key (default: env COLLABORATOR_API_KEY, then AppHost user secrets)
          --timeout <seconds>     Max wait for each workflow transition (default 300)
          --poll <ms>             Poll interval in milliseconds (default 1000)

        Request:
          --product <id>          Product id (default demo-product; created if missing)
          --product-name <name>   Product display name when creating
          --brief <text>          Product brief when creating
          --repository-url <url>  Optional HTTPS, SSH, or local file Git URL without embedded secrets
          --base-branch <name>    Branch to start features from (default: repository's default branch)
          --feature <text>        Feature request text (prompted when interactive and omitted)
          --run <guid>            Attach to an existing run instead of submitting a new feature

        Business analysis:
          --business-analyst <id> BA actor label (default "default")
          --ba-skill <id>         Skill ID (default "requirements-analysis")
          --ba-skill-prompt <text> Create or update the editable skill prompt (max 12000 chars)
          --architecture-skill-prompt <text> Create or update the architecture skill prompt

        Developer reviews:
          --specialist <role/domain/skill> Select a configured specialist (repeatable; omitted uses enabled skills)
          --no-specialists            Skip specialist analyses and synthesis for this run
          --reviewer-count <n>         Developers selected for this run (default 3; range 1-10)
          --developer <id|name|roles|focus> Configure developer profile (roles comma-separated; repeatable)
          --review-response <id=decision[:feedback]> Submit developer review (approve/revise; repeatable)

        Decisions (scripted; any of these disables interactive prompts):
          --feedback <text>       Ask the AI to revise the plan with this feedback (repeatable, applied in order)
          --approve               Approve the final plan (default after feedback is exhausted)
          --reject <reason>       Reject the final plan with a reason
          --auto                  Non-interactive: apply --feedback items then approve

        Output:
          --interactive           Force interactive prompts even when scripted options are present
          --quiet                 Only print phase markers, plans and the summary
          --verbose               Print HTTP calls and every poll
          -h, --help              Show this help

        Examples:
          dotnet run --project src/Collaborator.ApiClient
          dotnet run --project src/Collaborator.ApiClient -- --feature "Add CSV export" --feedback "Include a rollback step" --approve
          dotnet run --project src/Collaborator.ApiClient -- --developer "alice|Alice|security and privacy" --feature "Add CSV export"
          dotnet run --project src/Collaborator.ApiClient -- --run 3f2b... --reject "Out of scope"
        """;

    public static ClientOptions Parse(string[] args)
    {
        var options = new ClientOptions();
        var scripted = false;
        var forceInteractive = false;
        var envUrl = Environment.GetEnvironmentVariable("COLLABORATOR_API_URL");
        if (!string.IsNullOrWhiteSpace(envUrl))
        {
            options.BaseUrl = ParseUrl(envUrl);
        }
        var repositoryUrl = Environment.GetEnvironmentVariable("COLLABORATOR_PRODUCT_REPOSITORY_URL");
        if (!string.IsNullOrWhiteSpace(repositoryUrl))
        {
            options.ProductRepositoryUrl = repositoryUrl;
        }
        var baseBranch = Environment.GetEnvironmentVariable("COLLABORATOR_PRODUCT_BASE_BRANCH");
        if (!string.IsNullOrWhiteSpace(baseBranch))
        {
            options.RepositoryBaseBranch = baseBranch;
        }

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string Next()
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Option {arg} requires a value.");
                }
                return args[++i];
            }

            switch (arg)
            {
                case "-h" or "--help" or "/?": options.ShowHelp = true; break;
                case "--base-url": options.BaseUrl = ParseUrl(Next()); break;
                case "--api-key": options.ApiKey = Next(); break;
                case "--product": options.ProductId = Next(); break;
                case "--product-name": options.ProductName = Next(); break;
                case "--brief": options.ProductBrief = Next(); break;
                case "--repository-url": options.ProductRepositoryUrl = Next(); break;
                case "--base-branch": options.RepositoryBaseBranch = Next(); break;
                case "--feature": options.Feature = Next(); break;
                case "--business-analyst": options.BusinessAnalystId = Next(); break;
                case "--ba-skill": options.BusinessAnalystSkillId = Next(); break;
                case "--ba-skill-prompt": options.BusinessAnalystSkillPrompt = Next(); break;
                case "--architecture-skill-prompt": options.ArchitectureSkillPrompt = Next(); break;
                case "--reviewer-count": options.ReviewerCount = ParsePositive(Next(), arg); break;
                case "--developer": options.Developers.Add(ParseDeveloper(Next())); break;
                case "--specialist":
                    if (options.DeveloperSkills is { Count: 0 })
                    {
                        throw new ArgumentException("--specialist and --no-specialists cannot be combined.");
                    }
                    var parts = Next().Split('/');
                    if (parts.Length != 3)
                    {
                        throw new ArgumentException("--specialist requires role/domain/skill.");
                    }
                    options.DeveloperSkills ??= [];
                    options.DeveloperSkills.Add(new DeveloperSkillSelection(parts[0], parts[1], parts[2]));
                    break;
                case "--no-specialists":
                    if (options.DeveloperSkills is not null)
                    {
                        throw new ArgumentException("--no-specialists cannot be repeated or combined with --specialist.");
                    }
                    options.DeveloperSkills = [];
                    break;
                case "--review-response":
                    var reviewResponse = Next();
                    var separator = reviewResponse.IndexOf('=');
                    if (separator <= 0 || separator == reviewResponse.Length - 1 ||
                        !options.ReviewResponses.TryAdd(reviewResponse[..separator], reviewResponse[(separator + 1)..]))
                    {
                        throw new ArgumentException("--review-response requires a unique developer-id=nonempty-response value.");
                    }
                    scripted = true;
                    break;
                case "--run":
                    options.ExistingRunId = Guid.TryParse(Next(), out var runId)
                        ? runId : throw new ArgumentException("--run requires a GUID.");
                    break;
                case "--feedback": options.Feedback.Add(Next()); scripted = true; break;
                case "--approve": SetFinal(options, "approve", null); scripted = true; break;
                case "--reject": SetFinal(options, "reject", Next()); scripted = true; break;
                case "--auto": scripted = true; break;
                case "--interactive": forceInteractive = true; break;
                case "--quiet": options.Quiet = true; break;
                case "--verbose": options.Verbose = true; break;
                case "--timeout": options.Timeout = TimeSpan.FromSeconds(ParsePositive(Next(), arg)); break;
                case "--poll": options.PollInterval = TimeSpan.FromMilliseconds(ParsePositive(Next(), arg)); break;
                default: throw new ArgumentException($"Unknown option '{arg}'. Use --help for usage.");
            }
        }

        if (options.Quiet && options.Verbose)
        {
            throw new ArgumentException("--quiet and --verbose cannot be combined.");
        }
        if (options.ProductRepositoryUrl is not null && !InputRules.IsRepositoryUrl(options.ProductRepositoryUrl))
        {
            throw new ArgumentException("--repository-url must be an HTTPS, SSH, or local file URL without embedded secrets.");
        }
        if (!InputRules.IsRepositoryBaseBranch(options.RepositoryBaseBranch))
        {
            throw new ArgumentException("--base-branch must be a valid short Git branch name, such as main or develop.");
        }
        if (options.RepositoryBaseBranch is not null && options.ProductRepositoryUrl is null)
        {
            throw new ArgumentException("--base-branch requires --repository-url (or COLLABORATOR_PRODUCT_REPOSITORY_URL).");
        }
        if (!InputRules.IsText(options.BusinessAnalystId, 128) ||
            !InputRules.IsProductId(options.BusinessAnalystSkillId))
        {
            throw new ArgumentException("--business-analyst must be a nonempty ID (max 128 chars) and --ba-skill a lowercase ID.");
        }
        if (!InputRules.IsValidReviewerCount(options.ReviewerCount))
        {
            throw new ArgumentException($"--reviewer-count must be between 1 and {InputRules.MaximumReviewerCount}.");
        }
        if (options.BusinessAnalystSkillPrompt is not null &&
            !InputRules.IsText(options.BusinessAnalystSkillPrompt, InputRules.MaximumBusinessAnalystSkillPromptLength))
        {
            throw new ArgumentException(
                $"--ba-skill-prompt must be nonempty and at most {InputRules.MaximumBusinessAnalystSkillPromptLength} characters.");
        }
        if (options.ArchitectureSkillPrompt is not null &&
            !InputRules.IsText(options.ArchitectureSkillPrompt, InputRules.MaximumAgentSkillPromptLength))
        {
            throw new ArgumentException(
                $"--architecture-skill-prompt must be nonempty and at most {InputRules.MaximumAgentSkillPromptLength} characters.");
        }
        options.Interactive = forceInteractive || (!scripted && !Console.IsInputRedirected);
        if (!InputRules.IsValidFeatureSubmission(new FeatureSubmission(
            "Validate specialist selection.", DeveloperSkills: options.DeveloperSkills?.ToArray()), out var selectionError))
        {
            throw new ArgumentException(selectionError);
        }
        if (options.ExistingRunId is not null && options.DeveloperSkills is not null)
        {
            throw new ArgumentException("Specialist selection cannot be changed when attaching with --run.");
        }
        return options;
    }

    /// <summary>Resolves the key from the flag, environment, then local user-secrets files.</summary>
    public string? ResolveApiKey(out string source)
    {
        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            source = "--api-key";
            return ApiKey;
        }
        var env = Environment.GetEnvironmentVariable("COLLABORATOR_API_KEY");
        if (!string.IsNullOrWhiteSpace(env))
        {
            source = "COLLABORATOR_API_KEY";
            return env;
        }
        foreach (var (id, key) in new[] { (AppHostSecretsId, "Parameters:api-key"), (ApiSecretsId, "Security:ApiKey") })
        {
            var value = ReadUserSecret(id, key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                source = $"user-secrets ({id})";
                return value;
            }
        }
        source = "none";
        return null;
    }

    private static string? ReadUserSecret(string secretsId, string key)
    {
        var root = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".microsoft");
        var path = OperatingSystem.IsWindows()
            ? Path.Combine(root, "Microsoft", "UserSecrets", secretsId, "secrets.json")
            : Path.Combine(root, "usersecrets", secretsId, "secrets.json");
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty(key, out var flat) && flat.ValueKind == JsonValueKind.String)
            {
                return flat.GetString();
            }
            var current = doc.RootElement;
            foreach (var part in key.Split(':'))
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
                {
                    return null;
                }
            }
            return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void SetFinal(ClientOptions options, string decision, string? reason)
    {
        if (options.FinalDecision is not null && options.FinalDecision != decision)
        {
            throw new ArgumentException("--approve and --reject cannot be combined.");
        }
        options.FinalDecision = decision;
        options.RejectReason = reason;
    }

    private static Uri ParseUrl(string value) =>
        Uri.TryCreate(value.TrimEnd('/'), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri : throw new ArgumentException($"'{value}' is not an absolute http(s) URL.");

    private static DeveloperOption ParseDeveloper(string value)
    {
        var parts = value.Split('|', 4);
        if (parts.Length < 3)
        {
            throw new ArgumentException("--developer requires id|display-name|comma-separated-roles|optional-review-focus.");
        }
        var roles = parts[2].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var developer = new DeveloperConfiguration(
            parts[1], parts.Length == 4 ? parts[3] : null, Roles: roles);
        if (!InputRules.IsValidDeveloper(parts[0], developer, out var error))
        {
            throw new ArgumentException($"Invalid --developer value: {error}");
        }
        return new(parts[0].Trim(), developer);
    }

    private static int ParsePositive(string value, string option) =>
        int.TryParse(value, out var n) && n > 0 ? n : throw new ArgumentException($"{option} requires a positive integer.");
}

internal sealed record DeveloperOption(string UserId, DeveloperConfiguration Configuration);
