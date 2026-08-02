using System.IO.Compression;
using System.Security.Cryptography;

namespace AllyBindings.Core;

public static class UpdatePackageStager
{
    private const int MaximumEntries = 1_000;
    private const long MaximumExpandedBytes = 512L * 1024 * 1024;

    public static string VerifyAndExtract(string zipPath, string stagingDirectory, string expectedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        if (!GitHubReleaseUpdateSelector.TryParseSha256($"sha256:{expectedSha256}", out var normalizedExpected))
        {
            throw new InvalidDataException("The release SHA-256 digest is invalid.");
        }

        using var stream = OpenVerifiedReadStream(zipPath);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actual),
                Convert.FromHexString(normalizedExpected)))
        {
            throw new InvalidDataException("The downloaded update failed SHA-256 verification.");
        }
        stream.Position = 0;

        if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true);
        Directory.CreateDirectory(stagingDirectory);
        var stagingRoot = Path.GetFullPath(stagingDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expandedBytes = 0;

        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > MaximumEntries)
            {
                throw new InvalidDataException("The update archive contains too many files.");
            }

            foreach (var entry in archive.Entries)
            {
                expandedBytes = checked(expandedBytes + entry.Length);
                if (expandedBytes > MaximumExpandedBytes)
                {
                    throw new InvalidDataException("The update archive is unexpectedly large after extraction.");
                }

                var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
                if (unixType == 0xA000)
                {
                    throw new InvalidDataException("Symbolic links are not allowed in update archives.");
                }

                var destination = Path.GetFullPath(Path.Combine(stagingDirectory, entry.FullName));
                if (!destination.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase) ||
                    !seenPaths.Add(destination))
                {
                    throw new InvalidDataException($"Unsafe or duplicate update path: {entry.FullName}");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: false);
            }

            var executable = Directory.EnumerateFiles(stagingDirectory, "AllyBindings.exe", SearchOption.AllDirectories).SingleOrDefault()
                ?? throw new InvalidDataException("The update archive does not contain exactly one AllyBindings.exe.");
            return Path.GetDirectoryName(executable)!;
        }
        catch
        {
            if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true);
            throw;
        }
    }

    private static FileStream OpenVerifiedReadStream(string path)
    {
        const int maximumAttempts = 8;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (IOException) when (attempt < maximumAttempts)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(100 * attempt));
            }
        }
    }
}
