using System.Globalization;
using System.Threading;

namespace AllyBindings.Core;

public static class UsbEtwCapturePhaseCommand
{
    public const int MaximumCommandCharacters = 32;

    public static string Format(int phase, long boundaryQpc)
    {
        Validate(phase, boundaryQpc);
        return $"stage-{phase}:{boundaryQpc.ToString(CultureInfo.InvariantCulture)}";
    }

    public static bool TryParse(string command, out int phase, out long boundaryQpc)
    {
        phase = 0;
        boundaryQpc = 0;
        if (string.IsNullOrEmpty(command) || command.Length > MaximumCommandCharacters) return false;
        if (!command.StartsWith("stage-", StringComparison.Ordinal)) return false;

        var separator = command.IndexOf(':', "stage-".Length);
        if (separator != "stage-".Length + 1 || separator == command.Length - 1) return false;
        if (command["stage-".Length] is < '1' or > '3') return false;
        phase = command["stage-".Length] - '0';
        if (!long.TryParse(
                command.AsSpan(separator + 1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out boundaryQpc) ||
            boundaryQpc <= 0)
        {
            phase = 0;
            boundaryQpc = 0;
            return false;
        }
        return true;
    }

    private static void Validate(int phase, long boundaryQpc)
    {
        if (phase is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(phase));
        if (boundaryQpc <= 0) throw new ArgumentOutOfRangeException(nameof(boundaryQpc));
    }
}

/// <summary>
/// Stores capture-stage boundaries in the system QPC domain. Boundaries are
/// process-independent on Windows and remain in memory; only phase numbers are exported.
/// </summary>
public sealed class UsbEtwCapturePhaseBoundaries
{
    private readonly object _writeGate = new();
    private long _phase1Qpc = long.MaxValue;
    private long _phase2Qpc = long.MaxValue;
    private long _phase3Qpc = long.MaxValue;
    private int _highestRecordedPhase;

    public void Record(int phase, long boundaryQpc)
    {
        if (phase is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(phase));
        if (boundaryQpc <= 0) throw new ArgumentOutOfRangeException(nameof(boundaryQpc));

        lock (_writeGate)
        {
            var expectedPhase = _highestRecordedPhase + 1;
            if (phase != expectedPhase)
            {
                throw new InvalidDataException($"Expected capture phase {expectedPhase}, received phase {phase}.");
            }
            var previousBoundary = phase switch
            {
                1 => 0,
                2 => _phase1Qpc,
                3 => _phase2Qpc,
                _ => 0,
            };
            if (boundaryQpc < previousBoundary)
            {
                throw new InvalidDataException("Capture phase QPC boundaries must be monotonic.");
            }

            switch (phase)
            {
                case 1:
                    Volatile.Write(ref _phase1Qpc, boundaryQpc);
                    break;
                case 2:
                    Volatile.Write(ref _phase2Qpc, boundaryQpc);
                    break;
                case 3:
                    Volatile.Write(ref _phase3Qpc, boundaryQpc);
                    break;
            }
            Volatile.Write(ref _highestRecordedPhase, phase);
        }
    }

    public int Classify(long eventQpc)
    {
        var highestPhase = Volatile.Read(ref _highestRecordedPhase);
        if (highestPhase >= 3 && eventQpc >= Volatile.Read(ref _phase3Qpc)) return 3;
        if (highestPhase >= 2 && eventQpc >= Volatile.Read(ref _phase2Qpc)) return 2;
        if (highestPhase >= 1 && eventQpc >= Volatile.Read(ref _phase1Qpc)) return 1;
        return 0;
    }
}
