using System.Text.Json;

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
    public string? Feature { get; private set; }
    public Guid? ExistingRunId { get; private set; }
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

        Flow: health -> register product -> submit feature -> wait for plan -> decide
              (revise with feedback, repeat) -> approve/reject -> plan history + summary.

        Connection:
          --base-url <url>        API base URL (default http://localhost:5076; env COLLABORATOR_API_URL)
          --api-key <key>         API key (default: env COLLABORATOR_API_KEY, then AppHost user secrets)
          --timeout <seconds>     Max wait for each workflow transition (default 300)
          --poll <ms>             Poll interval in milliseconds (default 1000)

        Request:
          --product <id>          Product id (default demo-product; created if missing)
          --product-name <name>   Product display name when creating
          --brief <text>          Product brief when creating
          --feature <text>        Feature request text (prompted when interactive and omitted)
          --run <guid>            Attach to an existing run instead of submitting a new feature

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
                case "--feature": options.Feature = Next(); break;
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
        options.Interactive = forceInteractive || (!scripted && !Console.IsInputRedirected);
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

    private static int ParsePositive(string value, string option) =>
        int.TryParse(value, out var n) && n > 0 ? n : throw new ArgumentException($"{option} requires a positive integer.");
}
