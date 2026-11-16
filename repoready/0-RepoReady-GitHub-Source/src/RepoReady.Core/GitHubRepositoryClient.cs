using System.Net;
using System.Text;
using System.Text.Json;

namespace RepoReady.Core;

public sealed class GitHubRepositoryClient
{
    private readonly HttpClient _httpClient;

    public GitHubRepositoryClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("RepoReady/0.1.0");
        }

        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<RepositorySnapshot> GetSnapshotAsync(
        string repository,
        string? reference = null,
        CancellationToken cancellationToken = default)
    {
        var identifier = RepositoryIdentifier.Parse(repository);
        var repoPath = $"repos/{Uri.EscapeDataString(identifier.Owner)}/{Uri.EscapeDataString(identifier.Name)}";

        using var repositoryJson = await GetJsonAsync(
            $"https://api.github.com/{repoPath}",
            "repository metadata",
            cancellationToken);

        var metadata = ReadRequiredObject(repositoryJson.RootElement, "repository metadata");
        var defaultBranch = ReadRequiredString(metadata, "default_branch", "repository metadata");
        var requestedRef = string.IsNullOrWhiteSpace(reference) ? defaultBranch : reference;
        var escapedRef = Uri.EscapeDataString(requestedRef);

        using var commitJson = await GetJsonAsync(
            $"https://api.github.com/{repoPath}/commits/{escapedRef}",
            "commit lookup",
            cancellationToken);
        var commitResponse = ReadRequiredObject(commitJson.RootElement, "commit lookup");
        var commitSha = ReadRequiredString(commitResponse, "sha", "commit lookup");
        var commit = ReadRequiredObject(commitResponse, "commit", "commit lookup");
        var commitTree = ReadRequiredObject(commit, "tree", "commit lookup");
        var treeSha = ReadRequiredString(commitTree, "sha", "commit lookup");

        using var treeJson = await GetJsonAsync(
            $"https://api.github.com/repos/{Uri.EscapeDataString(identifier.Owner)}/{Uri.EscapeDataString(identifier.Name)}/git/trees/{Uri.EscapeDataString(treeSha)}?recursive=1",
            "repository tree",
            cancellationToken);

        var treeRoot = treeJson.RootElement;
        if (!treeRoot.TryGetProperty("truncated", out var truncatedProperty) ||
            truncatedProperty.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new RepoReadyException("GitHub returned an invalid tree truncation indicator.", "repository tree");
        }

        var treeTruncated = truncatedProperty.GetBoolean();
        if (!treeRoot.TryGetProperty("tree", out var treeEntries) || treeEntries.ValueKind != JsonValueKind.Array)
        {
            throw new RepoReadyException("GitHub returned an invalid repository tree response.", "repository tree");
        }

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in treeEntries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String)
            {
                throw new RepoReadyException("GitHub returned an invalid repository tree entry.", "repository tree");
            }

            if (type.GetString() != "blob")
            {
                continue;
            }

            if (!entry.TryGetProperty("path", out var pathProperty) ||
                pathProperty.ValueKind != JsonValueKind.String ||
                !entry.TryGetProperty("sha", out var shaProperty) ||
                shaProperty.ValueKind != JsonValueKind.String)
            {
                throw new RepoReadyException("GitHub returned an incomplete blob tree entry.", "repository tree");
            }

            var path = pathProperty.GetString();
            var sha = shaProperty.GetString();
            if (path is null || sha is null || !IsCandidate(path))
            {
                continue;
            }

            files[path] = await GetBlobTextAsync(identifier, sha, cancellationToken);
        }

        return new RepositorySnapshot(
            identifier.FullName,
            requestedRef,
            commitSha,
            treeTruncated,
            files);
    }

    private async Task<string> GetBlobTextAsync(
        RepositoryIdentifier repository,
        string blobSha,
        CancellationToken cancellationToken)
    {
        var owner = Uri.EscapeDataString(repository.Owner);
        var name = Uri.EscapeDataString(repository.Name);
        using var blobJson = await GetJsonAsync(
            $"https://api.github.com/repos/{owner}/{name}/git/blobs/{Uri.EscapeDataString(blobSha)}",
            "file content",
            cancellationToken);

        var root = blobJson.RootElement;
        if (!root.TryGetProperty("encoding", out var encoding) ||
            encoding.ValueKind != JsonValueKind.String ||
            encoding.GetString() != "base64" ||
            !root.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.String)
        {
            throw new RepoReadyException("GitHub returned an unsupported file content encoding.", "file content");
        }

        try
        {
            var bytes = Convert.FromBase64String(content.GetString() ?? string.Empty);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException exception)
        {
            throw new RepoReadyException("GitHub returned invalid base64 file content.", "file content", exception);
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string url, string stage, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(url, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new RepoReadyException($"GitHub API request failed during {stage}: {exception.Message}", stage, exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RepoReadyException($"GitHub API request timed out during {stage}.", stage, exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var detail = response.StatusCode switch
                {
                    HttpStatusCode.NotFound => "The repository or requested ref was not found or is not publicly accessible.",
                    HttpStatusCode.Forbidden => "GitHub API denied the request (rate limit or access restriction).",
                    HttpStatusCode.TooManyRequests => "GitHub API rate limit was exceeded.",
                    _ when (int)response.StatusCode >= 500 => $"GitHub API returned server error {(int)response.StatusCode}.",
                    _ => $"GitHub API returned HTTP {(int)response.StatusCode}."
                };
                throw new RepoReadyException($"GitHub API failure during {stage}: {detail}", stage);
            }

            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    document.Dispose();
                    throw new RepoReadyException($"GitHub returned an invalid JSON object during {stage}.", stage);
                }

                return document;
            }
            catch (JsonException exception)
            {
                throw new RepoReadyException($"GitHub returned invalid JSON during {stage}.", stage, exception);
            }
        }
    }

    private static bool IsCandidate(string path)
    {
        var normalized = path.Replace('\\', '/');
        var fileName = normalized[(normalized.LastIndexOf('/') + 1)..];
        var upperName = fileName.ToUpperInvariant();

        if (!normalized.Contains('/') && upperName.StartsWith("README", StringComparison.Ordinal))
        {
            return true;
        }

        if (upperName.StartsWith("CONTRIBUTING", StringComparison.Ordinal) &&
            (normalized.Count(character => character == '/') == 0 ||
             normalized.StartsWith("docs/", StringComparison.OrdinalIgnoreCase) ||
             normalized.StartsWith(".github/", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!normalized.Contains('/') &&
            (upperName.StartsWith("LICENSE", StringComparison.Ordinal) ||
             upperName.StartsWith("COPYING", StringComparison.Ordinal) ||
             upperName == "UNLICENSE"))
        {
            return true;
        }

        if (normalized.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase) &&
            (fileName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
             fileName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return normalized.StartsWith(".github/ISSUE_TEMPLATE/", StringComparison.OrdinalIgnoreCase) &&
            !fileName.Equals("config.yml", StringComparison.OrdinalIgnoreCase) &&
            !fileName.Equals("config.yaml", StringComparison.OrdinalIgnoreCase) &&
            (fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
             fileName.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
             fileName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadRequiredString(JsonElement element, string propertyName, string stage)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new RepoReadyException($"GitHub response is missing '{propertyName}' during {stage}.", stage);
        }

        return property.GetString()!;
    }

    private static JsonElement ReadRequiredObject(JsonElement element, string stage) =>
        element.ValueKind == JsonValueKind.Object
            ? element
            : throw new RepoReadyException($"GitHub returned an invalid object during {stage}.", stage);

    private static JsonElement ReadRequiredObject(JsonElement element, string propertyName, string stage)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Object)
        {
            throw new RepoReadyException($"GitHub response is missing '{propertyName}' during {stage}.", stage);
        }

        return property;
    }

}
