using System.Text;

namespace AllyBindings.Core;

/// <summary>
/// Durable fail-safe marker checked before virtual output is created. The marker
/// is independent from the main configuration transaction so a failed config
/// write cannot silently re-enable virtual remapping after restart.
/// </summary>
public sealed class VirtualRemappingSafetyLatch
{
    private readonly string _path;

    public VirtualRemappingSafetyLatch(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A latch path is required.", nameof(path));
        _path = Path.GetFullPath(path);
    }

    public bool IsSet => File.Exists(_path);

    public bool TrySet(string reason)
    {
        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var directory = Path.GetDirectoryName(_path)
                ?? throw new InvalidOperationException("Virtual recovery latch has no parent directory.");
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                temporaryPath,
                $"{DateTimeOffset.UtcNow:O}\n{reason}\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, _path, overwrite: true);
            return true;
        }
        catch
        {
            try { File.Delete(temporaryPath); } catch { }
            return false;
        }
    }

    public void Clear()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
