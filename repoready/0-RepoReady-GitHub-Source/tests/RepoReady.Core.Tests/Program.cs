using System.Net;
using System.Text;
using RepoReady.Core;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Repository URL parsing", TestRepositoryParsing),
    ("Complete snapshot passes recognized checks", TestPassingSnapshot),
    ("Case-variant repository paths do not collide", TestCaseVariantPaths),
    ("Complete snapshot fails absent files", TestMissingFilesFail),
    ("README prose is not mistaken for a make command", TestMakeMentionIsNotCommand),
    ("Opening README prose provides an introduction signal", TestReadmeIntroductionOpening),
    ("Named English and Chinese introduction sections are recognized", TestReadmeIntroductionHeadings),
    ("Headings, badges, setup sections, and code are not introduction prose", TestReadmeIntroductionNonSignals),
    ("Introduction signal respects the documented length threshold", TestReadmeIntroductionThreshold),
    ("Truncated README discovery keeps missing introduction signals unknown", TestReadmeIntroductionTruncated),
    ("Historical commit ignores default-branch license metadata", () => TestCommitLicense(false, false, CheckStatus.Failed)),
    ("Truncated historical tree cannot prove a missing license", () => TestCommitLicense(true, false, CheckStatus.Unknown)),
    ("Historical license file supplies commit-bound evidence", () => TestCommitLicense(false, true, CheckStatus.Passed)),
    ("Visible license in a truncated tree supplies commit-bound evidence", () => TestCommitLicense(true, true, CheckStatus.Passed)),
    ("Issue selector config is not a template", TestIssueSelectorOnly),
    ("Truncated tree never claims missing files", TestTruncatedTreeUnknown),
    ("GitHub snapshot reports resolved commit SHA", TestGitHubSnapshot),
    ("GitHub API errors remain scan errors", TestApiFailure)
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
return failures == 0 ? 0 : 1;

static Task TestRepositoryParsing()
{
    Equal("octo-cat/widget", RepositoryIdentifier.Parse("https://github.com/octo-cat/widget.git").FullName);
    Equal("octo-cat/widget", RepositoryIdentifier.Parse("octo-cat/widget").FullName);
    Throws<ArgumentException>(() => RepositoryIdentifier.Parse("http://github.com/octo-cat/widget"));
    return Task.CompletedTask;
}

static Task TestPassingSnapshot()
{
    var snapshot = Snapshot(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["README.md"] = "# Widget\nWidget is a command-line tool that summarizes repository metadata.\n## Installation\nRun `dotnet run`.",
        ["CONTRIBUTING.md"] = "Development and tests",
        ["LICENSE"] = "MIT License",
        [".github/workflows/ci.yml"] = "steps:\n  - run: dotnet test",
        [".github/ISSUE_TEMPLATE/bug.md"] = "## Bug report"
    });

    var results = new ReadinessCheck().Evaluate(snapshot);
    Equal(8, results.Count);
    True(results.All(result => result.Status == CheckStatus.Passed), "All known signals should pass.");
    var evidence = results.Single(result => result.Id == "readme-usage").Evidence;
    True(evidence.Contains("Installation", StringComparison.OrdinalIgnoreCase),
        $"The setup evidence should identify the matched README heading. Evidence: {evidence}");
    return Task.CompletedTask;
}

static Task TestMissingFilesFail()
{
    var results = new ReadinessCheck().Evaluate(Snapshot(new Dictionary<string, string>()));
    True(results.Where(result => result.Id != "readme-usage")
        .All(result => result.Status == CheckStatus.Failed), "A complete empty tree should fail missing-file checks.");
    return Task.CompletedTask;
}

static Task TestCaseVariantPaths()
{
    var snapshot = Snapshot(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["README.md"] = "# Project\n## Usage",
        ["Readme.md"] = "# Alternative project\n## Usage"
    });
    var results = new ReadinessCheck().Evaluate(snapshot);
    Equal(CheckStatus.Passed, results.Single(check => check.Id == "readme-file").Status);
    Equal(8, results.Count);
    return Task.CompletedTask;
}

static async Task TestCommitLicense(bool truncated, bool hasLicense, CheckStatus expected)
{
    const string historicalSha = "1111111111111111111111111111111111111111";
    var blob = Convert.ToBase64String(Encoding.UTF8.GetBytes("MIT License"));
    var entries = hasLicense
        ? """[{"path":"LICENSE","type":"blob","sha":"historical-license-blob"}]"""
        : "[]";
    using var client = new HttpClient(new StubHandler(request =>
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = path switch
        {
            "/repos/octo-cat/widget" => """{"default_branch":"main","license":{"spdx_id":"MIT"}}""",
            "/repos/octo-cat/widget/commits/" + historicalSha =>
                """{"sha":"1111111111111111111111111111111111111111","commit":{"tree":{"sha":"historical-tree"}}}""",
            "/repos/octo-cat/widget/git/trees/historical-tree" =>
                "{\"truncated\":" + truncated.ToString().ToLowerInvariant() + ",\"tree\":" + entries + "}",
            "/repos/octo-cat/widget/git/blobs/historical-license-blob" =>
                $$"""{"encoding":"base64","content":"{{blob}}"}""",
            _ => throw new InvalidOperationException($"Unexpected API request: {path}")
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }));
    var snapshot = await new GitHubRepositoryClient(client)
        .GetSnapshotAsync("octo-cat/widget", historicalSha);
    Equal(historicalSha, snapshot.CommitSha);
    var result = new ReadinessCheck().Evaluate(snapshot).Single(check => check.Id == "license");
    Equal(expected, result.Status);
    if (hasLicense)
    {
        True(result.Evidence.Contains(historicalSha) && result.Evidence.Contains("LICENSE"),
            "License evidence must identify both the target commit and its file.");
    }
}

static Task TestMakeMentionIsNotCommand()
{
    var snapshot = Snapshot(new Dictionary<string, string>
    {
        ["README.md"] = "# Project\nWe make useful tools for everyone."
    });
    var result = new ReadinessCheck().Evaluate(snapshot).Single(check => check.Id == "readme-usage");
    Equal(CheckStatus.Failed, result.Status);
    return Task.CompletedTask;
}

static CheckResult IntroductionResult(string readme, bool truncated = false) =>
    new ReadinessCheck().Evaluate(Snapshot(new Dictionary<string, string>
    {
        ["README.md"] = readme
    }, truncated)).Single(check => check.Id == "readme-introduction");

static Task TestReadmeIntroductionOpening()
{
    const string description = "Widget summarizes repository metadata for project maintainers.";
    var result = IntroductionResult("# Widget\n\n" + description + "\n## Installation\nnpm install");
    Equal(CheckStatus.Passed, result.Status);
    True(result.Evidence.Contains("README.md:3") && result.Evidence.Contains(description),
        "Introduction evidence must show the source line and observable text.");
    return Task.CompletedTask;
}

static Task TestReadmeIntroductionHeadings()
{
    foreach (var heading in new[] { "About", "oVeRvIeW", "Introduction", "Project description", "简介", "项目简介", "项目介绍", "概述" })
    {
        var result = IntroductionResult(
            "# Widget\n## Installation\nnpm install\n## " + heading +
            "\n这是一个帮助项目维护者整理公开仓库基础信息的命令行工具。");
        Equal(CheckStatus.Passed, result.Status);
        True(result.Evidence.Contains("README.md:5") && result.Evidence.Contains(heading),
            "Named-section evidence should identify the heading and line.");
    }

    return Task.CompletedTask;
}

static Task TestReadmeIntroductionNonSignals()
{
    var samples = new[]
    {
        "# Widget\n## Introduction",
        "# Widget\n## Usage\nRun this long installation command to start the project locally.",
        "# Widget\n[![Build status](https://example.com/status.svg)](https://example.com)",
        "# Widget\n```text\n## Introduction\nThis long code example is not introductory prose.\n```\n## Usage",
        "# Widget\n~~~text\nThis long code example is not introductory prose.\n~~~",
        "# Widget\n    This indented code sample is not introductory prose.",
        "# Widget\n<!--\nThis hidden comment is not visible introductory prose.\n-->",
        "# Widget\n- This long list entry is not supported introductory prose."
    };
    foreach (var sample in samples)
    {
        Equal(CheckStatus.Failed, IntroductionResult(sample).Status);
    }

    return Task.CompletedTask;
}

static Task TestReadmeIntroductionThreshold()
{
    Equal(CheckStatus.Failed, IntroductionResult("# Widget\n" + new string('a', 19)).Status);
    Equal(CheckStatus.Passed, IntroductionResult("# Widget\n" + new string('a', 20)).Status);
    return Task.CompletedTask;
}

static Task TestReadmeIntroductionTruncated()
{
    Equal(CheckStatus.Unknown, IntroductionResult("# Widget\n## Usage", truncated: true).Status);
    Equal(CheckStatus.Passed, IntroductionResult(
        "# Widget\nWidget summarizes repository metadata for maintainers.", truncated: true).Status);
    return Task.CompletedTask;
}

static Task TestIssueSelectorOnly()
{
    var snapshot = Snapshot(new Dictionary<string, string>
    {
        [".github/ISSUE_TEMPLATE/config.yml"] = "blank_issues_enabled: false"
    });
    var result = new ReadinessCheck().Evaluate(snapshot).Single(check => check.Id == "issue-template");
    Equal(CheckStatus.Failed, result.Status);
    return Task.CompletedTask;
}

static Task TestTruncatedTreeUnknown()
{
    var results = new ReadinessCheck().Evaluate(Snapshot(
        new Dictionary<string, string>(),
        treeTruncated: true));
    True(results.All(result => result.Status == CheckStatus.Unknown),
        "A truncated tree cannot prove that candidate files are absent.");
    return Task.CompletedTask;
}

static async Task TestGitHubSnapshot()
{
    var blob = Convert.ToBase64String(Encoding.UTF8.GetBytes("# Demo\n## Usage\n"));
    var requests = new List<string>();
    using var client = new HttpClient(new StubHandler(request =>
    {
        var path = request.RequestUri!.AbsolutePath;
        requests.Add(path);
        var body = path switch
        {
            "/repos/octo-cat/widget" => """{"default_branch":"main","license":null}""",
            "/repos/octo-cat/widget/commits/main" =>
                """{"sha":"abc123456789","commit":{"tree":{"sha":"tree123"}}}""",
            "/repos/octo-cat/widget/git/trees/tree123" =>
                """{"truncated":false,"tree":[{"path":"README.md","type":"blob","sha":"blob123"}]}""",
            "/repos/octo-cat/widget/git/blobs/blob123" =>
                $$"""{"encoding":"base64","content":"{{blob}}"}""",
            _ => throw new InvalidOperationException($"Unexpected API request: {path}")
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }));

    var snapshot = await new GitHubRepositoryClient(client)
        .GetSnapshotAsync("octo-cat/widget", "main");
    Equal("abc123456789", snapshot.CommitSha);
    Equal("main", snapshot.RequestedRef);
    Equal("# Demo\n## Usage\n", snapshot.Files["README.md"]);
    Equal(4, requests.Count);
}

static async Task TestApiFailure()
{
    using var client = new HttpClient(new StubHandler(_ =>
        new HttpResponseMessage(HttpStatusCode.Forbidden)));
    var github = new GitHubRepositoryClient(client);
    var exception = await ThrowsAsync<RepoReadyException>(
        () => github.GetSnapshotAsync("octo-cat/widget"));
    True(exception.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase),
        "The API failure should describe the restriction rather than an absent file.");
}

static RepositorySnapshot Snapshot(
    IReadOnlyDictionary<string, string> files,
    bool treeTruncated = false) =>
    new("octo-cat/widget", "main", "0123456789abcdef", treeTruncated, files);

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}

static void True(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void Throws<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

static async Task<TException> ThrowsAsync<TException>(Func<Task> action) where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException exception)
    {
        return exception;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}

sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) =>
        Task.FromResult(responseFactory(request));
}
