using System.Text;
using System.Text.RegularExpressions;

namespace RepoReady.Core;

public interface ICheckRule
{
    CheckResult Evaluate(RepositorySnapshot repository);
}

public sealed class ReadinessCheck
{
    private static readonly string[] TestCommands =
    [
        "dotnet test", "pytest", "python -m unittest", "npm test", "pnpm test",
        "yarn test", "cargo test", "go test", "mvn test", "gradle test", "make test"
    ];

    private static readonly HashSet<string> IntroductionHeadings = new(StringComparer.OrdinalIgnoreCase)
    {
        "About", "Overview", "Introduction", "Project description",
        "简介", "项目简介", "项目介绍", "概述"
    };

    public IReadOnlyList<CheckResult> Evaluate(RepositorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var readmes = FindFiles(snapshot, IsReadme);
        var readmeFile = FileCheck(snapshot, "readme-file", "README file", readmes,
            "Add a root README.md describing the project.", "No root README file was found.");
        var readmePath = readmes
            .OrderBy(path => path.Length)
            .ThenBy(path => path, StringComparer.Ordinal)
            .FirstOrDefault();
        var readmeText = readmePath is null ? null : snapshot.Files[readmePath];
        var readmeUsage = ContentCheck(
            snapshot,
            "readme-usage",
            "README setup or usage instructions",
            readmes.Count > 0,
            readmeText,
            @"(?im)^\s*#{1,6}\s+[^\r\n]*(?:install|getting started|usage|quick start|run|安装|运行|使用)[^\r\n]*",
            ["dotnet run", "npm install", "npm run", "pip install", "cargo run", "go run"],
            "Add clear prerequisites and installation or run steps to the README.",
            "README was found, but no recognized setup or usage instructions were detected.");

        var contributing = FindFiles(snapshot, IsContributing);
        var licenseFiles = FindFiles(snapshot, IsLicense);
        var workflows = FindFiles(snapshot, IsWorkflow);
        var issueTemplates = FindFiles(snapshot, IsIssueTemplate);

        var results = new List<CheckResult>
        {
            readmeFile,
            IntroductionCheck(snapshot, readmePath),
            readmeUsage,
            FileCheck(snapshot, "contributing-guide", "Contribution guide", contributing,
                "Add CONTRIBUTING.md with development setup, test commands, and contribution steps.",
                "No contribution guide was found in the repository root or common documentation locations."),
            LicenseCheck(snapshot, licenseFiles),
            FileCheck(snapshot, "ci-workflow", "CI workflow", workflows,
                "Add a GitHub Actions workflow that builds the project and runs checks on pull requests.",
                "No GitHub Actions workflow was found."),
            TestStepsCheck(snapshot, workflows),
            FileCheck(snapshot, "issue-template", "Issue template", issueTemplates,
                "Add a concrete bug-report or feature-request template under .github/ISSUE_TEMPLATE/.",
                "No concrete issue template was found (config.yml alone is only a template selector).")
        };

        return results;
    }

    private static CheckResult IntroductionCheck(RepositorySnapshot snapshot, string? path)
    {
        const string id = "readme-introduction";
        const string name = "README introduction signal";
        const string recommendation =
            "Describe the project's purpose in plain text before the first section, or under an About, Overview, Introduction, or Project description heading; use a line with at least 20 letters/digits.";
        if (path is null)
        {
            return MissingResult(snapshot, id, name, recommendation, "No root README was available to inspect.");
        }

        var lines = snapshot.Files[path].Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var introductionRegion = true;
        var firstHeading = true;
        var region = "opening section";
        var fenceCharacter = '\0';
        var fenceLength = 0;
        var inComment = false;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var text = line.Trim();
            var fence = Regex.Match(line, @"^ {0,3}(`{3,}|~{3,})(.*)$");
            if (fenceCharacter != '\0')
            {
                if (fence.Success && fence.Groups[1].Value[0] == fenceCharacter &&
                    fence.Groups[1].Length >= fenceLength &&
                    string.IsNullOrWhiteSpace(fence.Groups[2].Value))
                {
                    fenceCharacter = '\0';
                }

                continue;
            }

            if (inComment || text.Contains("<!--", StringComparison.Ordinal))
            {
                inComment = !text.Contains("-->", StringComparison.Ordinal);
                continue;
            }

            if (fence.Success)
            {
                fenceCharacter = fence.Groups[1].Value[0];
                fenceLength = fence.Groups[1].Length;
                continue;
            }

            if (line.StartsWith("    ", StringComparison.Ordinal) || line.StartsWith('\t'))
            {
                continue;
            }

            var heading = Regex.Match(line, @"^ {0,3}(#{1,6})[ \t]+(.+)$");
            if (heading.Success)
            {
                var title = heading.Groups[2].Value.TrimEnd('#', ' ', '\t');
                introductionRegion = (firstHeading && heading.Groups[1].Length == 1) ||
                    IntroductionHeadings.Contains(title);
                region = $"section '{title}'";
                firstHeading = false;
                continue;
            }

            if (!introductionRegion || text.Length == 0 ||
                text[0] is '#' or '>' or '<' or '[' or '!' or '`' or '|' or '-' or '+' or '*' or '=' or '~' ||
                Regex.IsMatch(text, @"^\d+[.)]\s") ||
                text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var letterDigitCount = text.EnumerateRunes().Count(Rune.IsLetterOrDigit);
            if (letterDigitCount >= 20)
            {
                var excerpt = string.Concat(text.EnumerateRunes().Take(160));
                if (text.EnumerateRunes().Count() > 160)
                {
                    excerpt += "...";
                }

                return Result(id, name, CheckStatus.Passed,
                    $"{path}:{index + 1}: found a plain-text line with {letterDigitCount} letters/digits in {region}: \"{excerpt}\". This is a structural signal, not a semantic assessment.",
                    recommendation);
            }
        }

        return MissingResult(snapshot, id, name, recommendation,
            $"No supported introductory prose line was detected in the selected README ({path}); this heuristic does not determine whether the text explains the project.");
    }

    private static CheckResult LicenseCheck(RepositorySnapshot snapshot, IReadOnlyList<string> files)
    {
        if (files.Count > 0)
        {
            var evidence = $"Found {string.Join(", ", files)} in commit {snapshot.CommitSha}.";
            return Result("license", "License", CheckStatus.Passed, evidence,
                "Keep the full license text in the repository; file presence does not validate its legal terms.");
        }

        return MissingResult(snapshot, "license", "License",
            "Choose a license and add its complete text as a root LICENSE file.",
            $"No common root license file was found in commit {snapshot.CommitSha}.");
    }

    private static CheckResult TestStepsCheck(RepositorySnapshot snapshot, IReadOnlyList<string> workflows)
    {
        foreach (var path in workflows)
        {
            var command = TestCommands.FirstOrDefault(candidate =>
                snapshot.Files[path].Contains(candidate, StringComparison.OrdinalIgnoreCase));
            if (command is not null)
            {
                return Result("test-steps", "CI test steps", CheckStatus.Passed,
                    $"Found '{command}' in {path}.",
                    "Keep automated tests running for changes and pull requests.");
            }
        }

        if (workflows.Count == 0)
        {
            return MissingResult(snapshot, "test-steps", "CI test steps",
                "Add a recognizable test command to a CI workflow.",
                "No workflow test command was detected.");
        }

        if (snapshot.TreeTruncated)
        {
            return Unknown("test-steps", "CI test steps",
                "The Git tree was truncated; not all workflow files could be inspected.",
                "Confirm that CI runs the project's test suite.");
        }

        return Result("test-steps", "CI test steps", CheckStatus.Failed,
            $"Inspected {workflows.Count} workflow file(s), but recognized no supported test command.",
            "Add a recognizable test command such as dotnet test, pytest, npm test, cargo test, or go test.");
    }

    private static CheckResult ContentCheck(
        RepositorySnapshot snapshot,
        string id,
        string name,
        bool fileExists,
        string? content,
        string headingPattern,
        IReadOnlyList<string> commandPatterns,
        string recommendation,
        string failureEvidence)
    {
        if (!fileExists || content is null)
        {
            return MissingResult(snapshot, id, name, recommendation, failureEvidence);
        }

        var heading = Regex.Match(content, headingPattern);
        if (heading.Success)
        {
            return Result(id, name, CheckStatus.Passed,
                $"Found recognized section: {heading.Value.Trim().TrimStart('#').Trim()}.", recommendation);
        }

        var command = FindSetupCommand(content, commandPatterns);
        if (command is not null)
        {
            return Result(id, name, CheckStatus.Passed,
                $"Found recognizable setup or run command: {command}.", recommendation);
        }

        return Result(id, name, CheckStatus.Failed, failureEvidence, recommendation);
    }

    private static string? FindSetupCommand(string content, IReadOnlyList<string> commandPatterns)
    {
        var alternatives = string.Join("|", commandPatterns
            .OrderByDescending(candidate => candidate.Length)
            .Select(Regex.Escape));
        var command = Regex.Match(content, $@"(?im)(?:^|[\s`])({alternatives})\b");
        if (command.Success)
        {
            return command.Groups[1].Value;
        }

        command = Regex.Match(content, @"(?im)^\s*(?:\$\s*)?(make(?:\s+(?:test|install|run|build|all))?)\s*$");
        return command.Success ? command.Groups[1].Value : null;
    }

    private static CheckResult FileCheck(
        RepositorySnapshot snapshot,
        string id,
        string name,
        IReadOnlyList<string> files,
        string recommendation,
        string failureEvidence)
    {
        if (files.Count > 0)
        {
            return Result(id, name, CheckStatus.Passed, $"Found {string.Join(", ", files)}.", recommendation);
        }

        return MissingResult(snapshot, id, name, recommendation, failureEvidence);
    }

    private static CheckResult MissingResult(
        RepositorySnapshot snapshot,
        string id,
        string name,
        string recommendation,
        string failureEvidence) =>
        snapshot.TreeTruncated
            ? Unknown(id, name,
                "The Git tree was truncated, so absence cannot be confirmed. " + failureEvidence,
                recommendation)
            : Result(id, name, CheckStatus.Failed, failureEvidence, recommendation);

    private static CheckResult Unknown(string id, string name, string evidence, string recommendation) =>
        Result(id, name, CheckStatus.Unknown, evidence, recommendation);

    private static CheckResult Result(
        string id,
        string name,
        CheckStatus status,
        string evidence,
        string recommendation) =>
        new(id, name, status, evidence, recommendation);

    private static IReadOnlyList<string> FindFiles(RepositorySnapshot snapshot, Func<string, bool> predicate) =>
        snapshot.Files.Keys
            .Where(predicate)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private static bool IsReadme(string path) =>
        !path.Contains('/') &&
        Path.GetFileName(path).StartsWith("README", StringComparison.OrdinalIgnoreCase);

    private static bool IsContributing(string path)
    {
        var normalized = path.Replace('\\', '/');
        var name = Path.GetFileName(normalized);
        return name.StartsWith("CONTRIBUTING", StringComparison.OrdinalIgnoreCase) &&
            (!normalized.Contains('/') ||
             normalized.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) ||
             normalized.StartsWith(".github/", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLicense(string path)
    {
        if (path.Contains('/'))
        {
            return false;
        }

        var name = Path.GetFileName(path);
        return name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("COPYING", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("UNLICENSE", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWorkflow(string path) =>
        path.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase) &&
        (path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
         path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));

    private static bool IsIssueTemplate(string path)
    {
        const string prefix = ".github/ISSUE_TEMPLATE/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = Path.GetFileName(path);
        return !name.Equals("config.yml", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("config.yaml", StringComparison.OrdinalIgnoreCase) &&
            (name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
             name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
             name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class RepositoryScanner(GitHubRepositoryClient client)
{
    private readonly ReadinessCheck _check = new();

    public async Task<ScanReport> ScanAsync(
        string repository,
        string? reference = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await client.GetSnapshotAsync(repository, reference, cancellationToken);
        return new ScanReport(
            snapshot.Repository,
            snapshot.RequestedRef,
            snapshot.CommitSha,
            snapshot.TreeTruncated,
            _check.Evaluate(snapshot));
    }
}
