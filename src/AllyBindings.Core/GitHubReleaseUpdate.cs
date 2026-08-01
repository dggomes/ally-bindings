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

public sealed record SemanticReleaseVersion(
    Version Core,
    string? Prerelease) : IComparable<SemanticReleaseVersion>
{
    public int CompareTo(SemanticReleaseVersion? other)
    {
        if (other is null) return 1;
        var coreComparison = Core.CompareTo(other.Core);
        if (coreComparison != 0) return coreComparison;
        if (Prerelease is null) return other.Prerelease is null ? 0 : 1;
        if (other.Prerelease is null) return -1;

        var left = Prerelease.Split('.');
        var right = other.Prerelease.Split('.');
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            var identifierComparison = CompareIdentifier(left[index], right[index]);
            if (identifierComparison != 0) return identifierComparison;
        }
        return left.Length.CompareTo(right.Length);
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = left.All(char.IsAsciiDigit);
        var rightNumeric = right.All(char.IsAsciiDigit);
        if (leftNumeric && rightNumeric)
        {
            var normalizedLeft = left.TrimStart('0');
            var normalizedRight = right.TrimStart('0');
            if (normalizedLeft.Length == 0) normalizedLeft = "0";
            if (normalizedRight.Length == 0) normalizedRight = "0";
            var lengthComparison = normalizedLeft.Length.CompareTo(normalizedRight.Length);
            return lengthComparison != 0
                ? lengthComparison
                : string.CompareOrdinal(normalizedLeft, normalizedRight);
        }
        if (leftNumeric != rightNumeric) return leftNumeric ? -1 : 1;
        return string.CompareOrdinal(left, right);
    }
}

public static partial class GitHubReleaseUpdateSelector
{
    public static UpdateCandidate? Select(
        IEnumerable<GitHubRelease> releases,
        Version currentVersion,
        bool includePrerelease,
        string expectedRepository = "dggomes/ally-bindings") =>
        Select(
            releases,
            new SemanticReleaseVersion(Normalize(currentVersion), null),
            includePrerelease,
            expectedRepository);

    public static UpdateCandidate? Select(
        IEnumerable<GitHubRelease> releases,
        string currentVersion,
        bool includePrerelease,
        string expectedRepository = "dggomes/ally-bindings")
    {
        var parsedCurrent = ParseSemanticVersion(currentVersion)
            ?? throw new ArgumentException("Current application version is not valid semantic versioning.", nameof(currentVersion));
        return Select(releases, parsedCurrent, includePrerelease, expectedRepository);
    }

    private static UpdateCandidate? Select(
        IEnumerable<GitHubRelease> releases,
        SemanticReleaseVersion currentVersion,
        bool includePrerelease,
        string expectedRepository)
    {
        var expectedReleasePrefix = $"/{expectedRepository.Trim('/')}/releases/";
        foreach (var release in releases
                     .Where(release => !release.Draft && (includePrerelease || !release.Prerelease))
                     .Select(release => (Release: release, Version: ParseSemanticVersion(release.TagName)))
                     .Where(candidate => candidate.Version is not null && candidate.Version.CompareTo(currentVersion) > 0)
                     .OrderByDescending(candidate => candidate.Version)
                     .ThenByDescending(candidate => candidate.Release.PublishedAt))
        {
            var tagName = release.Release.TagName;
            var expectedAssetName = $"AllyBindings-{tagName}-win-x64.zip";
            var expectedReleasePath = $"{expectedReleasePrefix}tag/{tagName}";
            var expectedDownloadPath = $"{expectedReleasePrefix}download/{tagName}/{expectedAssetName}";

            if (release.Release.Prerelease != (release.Version!.Prerelease is not null))
            {
                continue;
            }
            var asset = release.Release.Assets.FirstOrDefault(asset =>
            {
                var parsed = Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var uri);
                var matches = asset.Name.Equals(expectedAssetName, StringComparison.Ordinal) &&
                    parsed &&
                    uri!.Scheme == Uri.UriSchemeHttps &&
                    uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
                    Uri.UnescapeDataString(uri.AbsolutePath).Equals(expectedDownloadPath, StringComparison.Ordinal) &&
                    string.IsNullOrEmpty(uri.Query) &&
                    string.IsNullOrEmpty(uri.Fragment) &&
                    TryParseSha256(asset.Digest, out _);

                return matches;
            });
            var releasePageParsed = Uri.TryCreate(release.Release.HtmlUrl, UriKind.Absolute, out var releasePage);
            var downloadParsed = Uri.TryCreate(asset?.BrowserDownloadUrl, UriKind.Absolute, out var downloadUrl);

            if (asset is null ||
                !releasePageParsed ||
                !downloadParsed ||
                releasePage!.Scheme != Uri.UriSchemeHttps ||
                !releasePage.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
                !Uri.UnescapeDataString(releasePage.AbsolutePath).TrimEnd('/').Equals(expectedReleasePath, StringComparison.Ordinal) ||
                !string.IsNullOrEmpty(releasePage.Query) ||
                !string.IsNullOrEmpty(releasePage.Fragment) ||
                !TryParseSha256(asset.Digest, out var sha256))
            {
                continue;
            }

            return new UpdateCandidate(
                release.Version!.Core,
                release.Release.TagName,
                string.IsNullOrWhiteSpace(release.Release.Name) ? release.Release.TagName : release.Release.Name,
                releasePage,
                downloadUrl!,
                sha256,
                release.Release.Prerelease);
        }
        return null;
    }

    public static Version? ParseVersion(string? tag) => ParseSemanticVersion(tag)?.Core;

    public static SemanticReleaseVersion? ParseSemanticVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = VersionTag().Match(value.Trim());
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, out var major) ||
            !int.TryParse(match.Groups[2].Value, out var minor) ||
            !int.TryParse(match.Groups[3].Value, out var patch)) return null;
        return new(
            new Version(major, minor, patch, 0),
            match.Groups[4].Success ? match.Groups[4].Value : null);
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

    private static Version Normalize(Version version) =>
        new(
            version.Major,
            version.Minor,
            Math.Max(version.Build, 0),
            Math.Max(version.Revision, 0));

    [GeneratedRegex("^v?(0|[1-9]\\d*)\\.(0|[1-9]\\d*)\\.(0|[1-9]\\d*)(?:-((?:0|[1-9]\\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\\.(?:0|[1-9]\\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\\+([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionTag();

    [GeneratedRegex("^sha256:([a-f0-9]{64})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Digest();

}
