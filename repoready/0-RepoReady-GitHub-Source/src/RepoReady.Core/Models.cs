namespace RepoReady.Core;

public enum CheckStatus
{
    Passed,
    Failed,
    Unknown
}

public sealed record CheckResult(
    string Id,
    string Name,
    CheckStatus Status,
    string Evidence,
    string Recommendation);

public sealed record RepositorySnapshot(
    string Repository,
    string RequestedRef,
    string CommitSha,
    bool TreeTruncated,
    IReadOnlyDictionary<string, string> Files);

public sealed record ScanReport(
    string Repository,
    string Ref,
    string CommitSha,
    bool TreeTruncated,
    IReadOnlyList<CheckResult> Checks);

public sealed class RepoReadyException(string message, string stage, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Stage { get; } = stage;
}

public sealed record RepositoryIdentifier(string Owner, string Name)
{
    public string FullName => $"{Owner}/{Name}";

    public static RepositoryIdentifier Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        string[] segments;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme != Uri.UriSchemeHttps ||
                !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                uri.Port != 443 ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment))
            {
                throw new ArgumentException("Repository URL must be an HTTPS github.com URL.", nameof(value));
            }

            segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 2 && segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                segments[1] = segments[1][..^4];
            }
        }
        else
        {
            segments = value.Split('/', StringSplitOptions.None);
        }

        if (segments.Length != 2 || !IsValidSegment(segments[0]) || !IsValidSegment(segments[1]))
        {
            throw new ArgumentException("Repository must be written as owner/repo or an HTTPS github.com URL.", nameof(value));
        }

        return new RepositoryIdentifier(segments[0], segments[1]);
    }

    private static bool IsValidSegment(string segment) =>
        segment.Length is > 0 and <= 100 &&
        segment.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}
