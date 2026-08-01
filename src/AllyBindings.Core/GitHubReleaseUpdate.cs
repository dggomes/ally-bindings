using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AllyBindings.Core;

public sealed record GitHubReleaseAsset
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; init; } = string.Empty;

    [JsonPropertyName("digest")]
    public string? Digest { get; init; }
}

public sealed record GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("draft")]
    public bool Draft { get; init; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; init; }

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; init; } = string.Empty;

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; init; }

    [JsonPropertyName("assets")]
    public List<GitHubReleaseAsset> Assets { get; init; } = [];
}

public sealed record UpdateCandidate(
    Version Version,
    string TagName,
    string ReleaseName,
    Uri ReleasePage,
    Uri DownloadUrl,
    string Sha256,
    bool IsPrerelease);

public static partial class GitHubReleaseUpdateSelector
{
    public static UpdateCandidate? Select(
        IEnumerable<GitHubRelease> releases,
        Version currentVersion,
        bool includePrerelease,
        string expectedRepository = "dggomes/ally-bindings")
    {
        var expectedReleasePrefix = $"/{expectedRepository.Trim('/')}/releases/";
        foreach (var release in releases
                     .Where(release => !release.Draft && (includePrerelease || !release.Prerelease))
                     .Select(release => (Release: release, Version: ParseVersion(release.TagName)))
                     .Where(candidate => candidate.Version is not null && candidate.Version > currentVersion)
                     .OrderByDescending(candidate => candidate.Version)
                     .ThenByDescending(candidate => candidate.Release.PublishedAt))
        {
            var asset = release.Release.Assets.FirstOrDefault(asset =>
                WindowsPackageName().IsMatch(asset.Name) &&
                Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var uri) &&
                uri.Scheme == Uri.UriSchemeHttps &&
                uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
                uri.AbsolutePath.StartsWith(expectedReleasePrefix, StringComparison.OrdinalIgnoreCase) &&
                TryParseSha256(asset.Digest, out _));
            if (asset is null ||
                !Uri.TryCreate(release.Release.HtmlUrl, UriKind.Absolute, out var releasePage) ||
                !Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var downloadUrl) ||
                !releasePage.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                !releasePage.AbsolutePath.StartsWith(expectedReleasePrefix, StringComparison.OrdinalIgnoreCase) ||
                !TryParseSha256(asset.Digest, out var sha256))
            {
                continue;
            }

            return new UpdateCandidate(
                release.Version!,
                release.Release.TagName,
                string.IsNullOrWhiteSpace(release.Release.Name) ? release.Release.TagName : release.Release.Name,
                releasePage,
                downloadUrl,
                sha256,
                release.Release.Prerelease);
        }
        return null;
    }

    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var match = VersionTag().Match(tag.Trim());
        if (!match.Success) return null;
        return Version.TryParse(match.Groups[1].Value, out var version) ? version : null;
    }

    public static bool TryParseSha256(string? digest, out string sha256)
    {
        sha256 = string.Empty;
        if (string.IsNullOrWhiteSpace(digest)) return false;
        var match = Sha256Digest().Match(digest.Trim());
        if (!match.Success) return false;
        sha256 = match.Groups[1].Value.ToLowerInvariant();
        return true;
    }

    [GeneratedRegex("^v?(\\d+(?:\\.\\d+){1,3})(?:[-+].*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionTag();

    [GeneratedRegex("^sha256:([a-f0-9]{64})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Digest();

    [GeneratedRegex("^AllyBindings(?:-v[^-]+)?-win-x64\\.zip$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPackageName();
}
