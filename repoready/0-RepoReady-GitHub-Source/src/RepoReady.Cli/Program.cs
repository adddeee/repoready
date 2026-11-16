using System.Text.Json;
using System.Text.Json.Serialization;
using RepoReady.Core;

namespace RepoReady.Cli;

internal static class Program
{
    private const string Version = "0.1.0";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = ParseArguments(args);
            if (options.ShowHelp)
            {
                Console.Out.WriteLine(HelpText);
                return 0;
            }

            if (options.ShowVersion)
            {
                Console.Out.WriteLine(Version);
                return 0;
            }

            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var scanner = new RepositoryScanner(new GitHubRepositoryClient(httpClient));
            var report = await scanner.ScanAsync(options.Repository!, options.Reference);

            if (options.Format == "json")
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
            }
            else
            {
                WriteTextReport(report);
            }

            return ShouldFail(report, options.FailOn) ? 1 : 0;
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"Argument error: {exception.Message}");
            Console.Error.WriteLine(HelpText);
            return 2;
        }
        catch (RepoReadyException exception)
        {
            Console.Error.WriteLine($"Scan failed during {exception.Stage}: {exception.Message}");
            return 2;
        }
    }

    private static CliOptions ParseArguments(string[] args)
    {
        if (args.Length == 0 || args.Any(argument => argument is "--help" or "-h"))
        {
            return new CliOptions(ShowHelp: true);
        }

        if (args.Length == 1 && args[0] == "--version")
        {
            return new CliOptions(ShowVersion: true);
        }

        if (args[0] != "check")
        {
            throw new ArgumentException("Expected the 'check' command.");
        }

        string? repository = null;
        string? reference = null;
        var format = "text";
        var failOn = "failed";
        var seenOptions = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument is "--help" or "-h")
            {
                return new CliOptions(ShowHelp: true);
            }

            if (!seenOptions.Add(argument))
            {
                throw new ArgumentException($"Option '{argument}' may only be specified once.");
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Option '{argument}' requires a value.");
            }

            var value = args[++index];
            switch (argument)
            {
                case "--repo":
                    repository = value;
                    break;
                case "--ref":
                    reference = value;
                    break;
                case "--format":
                    format = value.ToLowerInvariant();
                    break;
                case "--fail-on":
                    failOn = value.ToLowerInvariant();
                    break;
                default:
                    throw new ArgumentException($"Unknown or repeated option '{argument}'.");
            }
        }

        if (repository is null)
        {
            throw new ArgumentException("Option '--repo' is required.");
        }

        if (reference is not null && string.IsNullOrWhiteSpace(reference))
        {
            throw new ArgumentException("Option '--ref' cannot be empty.");
        }

        if (format is not ("text" or "json"))
        {
            throw new ArgumentException("Option '--format' must be 'text' or 'json'.");
        }

        if (failOn is not ("none" or "failed" or "unknown"))
        {
            throw new ArgumentException("Option '--fail-on' must be 'none', 'failed', or 'unknown'.");
        }

        _ = RepositoryIdentifier.Parse(repository);
        return new CliOptions(repository, reference, format, failOn);
    }

    private static bool ShouldFail(ScanReport report, string threshold) =>
        threshold switch
        {
            "none" => false,
            "failed" => report.Checks.Any(check => check.Status == CheckStatus.Failed),
            "unknown" => report.Checks.Any(check => check.Status is CheckStatus.Failed or CheckStatus.Unknown),
            _ => throw new InvalidOperationException("Validated failure threshold was not recognized.")
        };

    private static void WriteTextReport(ScanReport report)
    {
        Console.Out.WriteLine($"RepoReady check: {report.Repository} @ {report.CommitSha}");
        Console.Out.WriteLine($"Requested ref: {report.Ref}");
        if (report.TreeTruncated)
        {
            Console.Out.WriteLine("Warning: GitHub returned a truncated tree; some absence checks may be Unknown.");
        }

        foreach (var check in report.Checks)
        {
            Console.Out.WriteLine();
            Console.Out.WriteLine($"[{check.Status}] {check.Name} ({check.Id})");
            Console.Out.WriteLine($"  Evidence: {check.Evidence}");
            Console.Out.WriteLine($"  Recommendation: {check.Recommendation}");
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private const string HelpText = """
        RepoReady - explainable readiness checks for public GitHub repositories

        Usage:
          repoready check --repo <owner/repo|https://github.com/owner/repo> [options]

        Options:
          --ref <ref>          Branch, tag, or commit; defaults to the repository default branch
          --format <format>    text (default) or json
          --fail-on <level>    none, failed (default), or unknown
          --help               Show help
          --version            Show version

        Exit codes: 0 scan completed below the selected threshold; 1 threshold reached;
                    2 invalid arguments or scan failure.
        """;

    private sealed record CliOptions(
        string? Repository = null,
        string? Reference = null,
        string Format = "text",
        string FailOn = "failed",
        bool ShowHelp = false,
        bool ShowVersion = false);
}
