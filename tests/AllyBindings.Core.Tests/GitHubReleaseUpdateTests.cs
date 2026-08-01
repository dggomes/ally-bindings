using System.IO.Compression;
using System.Security.Cryptography;
using AllyBindings.Core;

namespace AllyBindings.Core.Tests;

public sealed class GitHubReleaseUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ally-bindings-update-tests-{Guid.NewGuid():N}");

    [Fact]
    public void Selects_newer_verified_windows_zip_including_preview_release()
    {
        var release = new GitHubRelease
        {
            TagName = "v0.02",
            Name = "Preview 0.02",
            Prerelease = true,
            HtmlUrl = "https://github.com/dggomes/ally-bindings-releases/releases/tag/v0.02",
            Assets =
            [
                new GitHubReleaseAsset
                {
                    Name = "AllyBindings-v0.02-win-x64.zip",
                    BrowserDownloadUrl = "https://github.com/dggomes/ally-bindings-releases/releases/download/v0.02/AllyBindings-v0.02-win-x64.zip",
                    Digest = $"sha256:{new string('a', 64)}",
                },
            ],
        };

        var candidate = GitHubReleaseUpdateSelector.Select([release], new Version(0, 1), includePrerelease: true);

        Assert.NotNull(candidate);
        Assert.Equal(new Version(0, 2), candidate.Version);
        Assert.Equal(new string('a', 64), candidate.Sha256);
    }

    [Fact]
    public void Rejects_preview_when_channel_is_stable_or_digest_is_missing()
    {
        var release = new GitHubRelease
        {
            TagName = "v0.02",
            Prerelease = true,
            HtmlUrl = "https://github.com/dggomes/ally-bindings-releases/releases/tag/v0.02",
            Assets =
            [
                new GitHubReleaseAsset
                {
                    Name = "AllyBindings-v0.02-win-x64.zip",
                    BrowserDownloadUrl = "https://github.com/dggomes/ally-bindings-releases/releases/download/v0.02/file.zip",
                },
            ],
        };

        Assert.Null(GitHubReleaseUpdateSelector.Select([release], new Version(0, 1), includePrerelease: false));
        Assert.Null(GitHubReleaseUpdateSelector.Select([release], new Version(0, 1), includePrerelease: true));
    }

    [Fact]
    public void Rejects_verified_asset_outside_the_pinned_release_repository()
    {
        var release = new GitHubRelease
        {
            TagName = "v0.02",
            HtmlUrl = "https://github.com/attacker/releases/releases/tag/v0.02",
            Assets =
            [
                new GitHubReleaseAsset
                {
                    Name = "AllyBindings-v0.02-win-x64.zip",
                    BrowserDownloadUrl = "https://github.com/attacker/releases/releases/download/v0.02/AllyBindings-v0.02-win-x64.zip",
                    Digest = $"sha256:{new string('a', 64)}",
                },
            ],
        };

        Assert.Null(GitHubReleaseUpdateSelector.Select([release], new Version(0, 1), includePrerelease: true));
    }

    [Fact]
    public void Verified_package_extracts_and_returns_executable_directory()
    {
        var zip = CreateArchive(archive =>
        {
            var executable = archive.CreateEntry("AllyBindings/AllyBindings.exe");
            using var writer = new StreamWriter(executable.Open());
            writer.Write("test executable");
        });
        var staging = Path.Combine(_root, "staging");

        var packageRoot = UpdatePackageStager.VerifyAndExtract(zip, staging, Sha256(zip));

        Assert.True(File.Exists(Path.Combine(packageRoot, "AllyBindings.exe")));
    }

    [Fact]
    public void Checksum_mismatch_changes_no_staged_files()
    {
        var zip = CreateArchive(archive => archive.CreateEntry("AllyBindings.exe"));
        var staging = Path.Combine(_root, "staging-mismatch");

        Assert.Throws<InvalidDataException>(() =>
            UpdatePackageStager.VerifyAndExtract(zip, staging, new string('0', 64)));
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public void Zip_traversal_is_rejected_without_writing_outside_staging()
    {
        var zip = CreateArchive(archive =>
        {
            var executable = archive.CreateEntry("AllyBindings.exe");
            using (var writer = new StreamWriter(executable.Open())) writer.Write("safe");
            archive.CreateEntry("../escape.txt");
        });
        var staging = Path.Combine(_root, "nested", "staging");
        var escaped = Path.Combine(_root, "nested", "escape.txt");

        Assert.Throws<InvalidDataException>(() =>
            UpdatePackageStager.VerifyAndExtract(zip, staging, Sha256(zip)));
        Assert.False(File.Exists(escaped));
        Assert.False(Directory.Exists(staging));
    }

    private string CreateArchive(Action<ZipArchive> populate)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        populate(archive);
        return path;
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
