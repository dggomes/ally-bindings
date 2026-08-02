using System.Diagnostics;
using System.Globalization;

namespace AllyBindings.Core;

public enum UsbEtwCapturePhaseTransition
{
    Start,
    End,
}

public static class UsbEtwCapturePhaseCommand
{
    public const int MaximumCommandCharacters = 16;

    public static string Format(int phase, UsbEtwCapturePhaseTransition transition)
    {
        ValidatePhase(phase);
        var verb = transition == UsbEtwCapturePhaseTransition.Start ? "start" : "end";
        return string.Create(CultureInfo.InvariantCulture, $"{verb}-{phase}");
    }

    public static bool TryParse(
        string? command,
        out int phase,
        out UsbEtwCapturePhaseTransition transition)
    {
        phase = 0;
        transition = default;
        if (string.IsNullOrEmpty(command) || command.Length > MaximumCommandCharacters) return false;

        var separator = command.LastIndexOf('-');
        if (separator <= 0 || separator == command.Length - 1) return false;
        var verb = command[..separator];
        if (verb.Equals("start", StringComparison.Ordinal))
        {
            transition = UsbEtwCapturePhaseTransition.Start;
        }
        else if (verb.Equals("end", StringComparison.Ordinal))
        {
            transition = UsbEtwCapturePhaseTransition.End;
        }
        else
        {
            return false;
        }

        if (!int.TryParse(command.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out phase) ||
            phase is < 1 or > 3)
        {
            return false;
        }
        return command.Equals(Format(phase, transition), StringComparison.Ordinal);
    }

    private static void ValidatePhase(int phase)
    {
        if (phase is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(phase));
    }
}

/// <summary>
/// Tracks three closed action windows in the system-wide Windows QPC domain.
/// Both transitions and classification are serialized by the same lock. A transition's
/// QPC is sampled while holding that lock, so an ETW callback can never classify an
/// event on the wrong side of a boundary merely because of thread scheduling.
/// </summary>
public sealed class UsbEtwCapturePhaseWindows
{
    private readonly object _gate = new();
    private readonly long[] _starts = [long.MaxValue, long.MaxValue, long.MaxValue];
    private readonly long[] _ends = [long.MaxValue, long.MaxValue, long.MaxValue];
    private int _activePhase;
    private int _completedPhases;

    public long StartNow(int phase) => TransitionNow(phase, UsbEtwCapturePhaseTransition.Start);

    public long EndNow(int phase) => TransitionNow(phase, UsbEtwCapturePhaseTransition.End);

    public void StartAt(int phase, long qpc) => TransitionAt(phase, UsbEtwCapturePhaseTransition.Start, qpc);

    public void EndAt(int phase, long qpc) => TransitionAt(phase, UsbEtwCapturePhaseTransition.End, qpc);

    public int Classify(long eventQpc)
    {
        lock (_gate)
        {
            for (var phase = 3; phase >= 1; phase--)
            {
                var index = phase - 1;
                if (eventQpc >= _starts[index] && eventQpc < _ends[index]) return phase;
            }
            return 0;
        }
    }

    private long TransitionNow(int phase, UsbEtwCapturePhaseTransition transition)
    {
        lock (_gate)
        {
            var qpc = Stopwatch.GetTimestamp();
            TransitionLocked(phase, transition, qpc);
            return qpc;
        }
    }

    private void TransitionAt(int phase, UsbEtwCapturePhaseTransition transition, long qpc)
    {
        lock (_gate)
        {
            TransitionLocked(phase, transition, qpc);
        }
    }

    private void TransitionLocked(int phase, UsbEtwCapturePhaseTransition transition, long qpc)
    {
        if (phase is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(phase));
        if (qpc <= 0) throw new ArgumentOutOfRangeException(nameof(qpc));

        if (transition == UsbEtwCapturePhaseTransition.Start)
        {
            if (_activePhase != 0 || phase != _completedPhases + 1)
            {
                throw new InvalidOperationException("Capture phases must start sequentially and cannot overlap.");
            }
            var previousEnd = phase == 1 ? 0 : _ends[phase - 2];
            if (qpc < previousEnd) throw new InvalidOperationException("Capture phase boundaries must be monotonic.");
            _starts[phase - 1] = qpc;
            _activePhase = phase;
            return;
        }

        if (_activePhase != phase)
        {
            throw new InvalidOperationException("Only the active capture phase can end.");
        }
        if (qpc < _starts[phase - 1])
        {
            throw new InvalidOperationException("Capture phase boundaries must be monotonic.");
        }
        _ends[phase - 1] = qpc;
        _activePhase = 0;
        _completedPhases = phase;
    }
}
