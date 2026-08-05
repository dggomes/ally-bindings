using System.Runtime.InteropServices;
using System.Windows.Threading;
using AllyBindings.Core;

namespace AllyBindings.Windows;

public sealed class XInputMonitor : IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly System.Threading.Timer _safetyTimer;
    private readonly object _stateGate = new();
    private int? _preferredIndex;
    private int? _activeControllerIndex;
    private int _safetyPollActive;
    private volatile bool _disposed;

    public XInputMonitor(int? preferredIndex)
    {
        _preferredIndex = preferredIndex;
        _timer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(20),
        };
        _timer.Tick += (_, _) => Poll();
        _safetyTimer = new System.Threading.Timer(
            _ => PollSafety(),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    public int? ActiveControllerIndex
    {
        get { lock (_stateGate) return _activeControllerIndex; }
        private set { lock (_stateGate) _activeControllerIndex = value; }
    }
    public event EventHandler<ControllerSnapshot>? SnapshotReceived;
    public event EventHandler<ControllerSnapshot>? SafetySnapshotReceived;
    public event EventHandler<int?>? ActiveControllerChanged;

    public void Start()
    {
        _timer.Start();
        _safetyTimer.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(16));
    }

    public void Stop()
    {
        _timer.Stop();
        _safetyTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void SetPreferredIndex(int? index)
    {
        if (index is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(index));
        lock (_stateGate) _preferredIndex = index;
    }

    public static int? FindFirstConnectedIndex(int? preferredIndex = null)
    {
        var indices = preferredIndex is >= 0 and <= 3
            ? new[] { preferredIndex.Value }
            : new[] { 0, 1, 2, 3 };
        foreach (var index in indices)
        {
            if (TryRead(index, out _)) return index;
        }
        return null;
    }

    private void Poll()
    {
        if (_disposed)
        {
            return;
        }

        int? preferredIndex;
        lock (_stateGate) preferredIndex = _preferredIndex;
        var indices = preferredIndex.HasValue
            ? new[] { preferredIndex.Value }
            : new[] { 0, 1, 2, 3 };

        foreach (var index in indices)
        {
            if (TryRead(index, out var snapshot))
            {
                if (ActiveControllerIndex != index)
                {
                    ActiveControllerIndex = index;
                    ActiveControllerChanged?.Invoke(this, index);
                }
                SnapshotReceived?.Invoke(this, snapshot);
                return;
            }
        }

        if (ActiveControllerIndex is not null)
        {
            ActiveControllerIndex = null;
            ActiveControllerChanged?.Invoke(this, null);
        }
        SnapshotReceived?.Invoke(this, ControllerSnapshot.Disconnected);
    }

    private void PollSafety()
    {
        if (_disposed || Interlocked.Exchange(ref _safetyPollActive, 1) != 0) return;
        try
        {
            int? preferredIndex;
            lock (_stateGate) preferredIndex = _preferredIndex;
            var index = FindFirstConnectedIndex(preferredIndex);
            if (index is not null && TryRead(index.Value, out var snapshot))
                SafetySnapshotReceived?.Invoke(this, snapshot);
            else
                SafetySnapshotReceived?.Invoke(this, ControllerSnapshot.Disconnected);
        }
        finally
        {
            Interlocked.Exchange(ref _safetyPollActive, 0);
        }
    }

    private static bool TryRead(int index, out ControllerSnapshot snapshot)
    {
        try
        {
            var result = XInputGetState((uint)index, out var state);
            if (result != 0)
            {
                snapshot = ControllerSnapshot.Disconnected;
                return false;
            }

            var gamepad = state.Gamepad;
            snapshot = new ControllerSnapshot(
                true,
                (ControllerButton)gamepad.Buttons,
                gamepad.LeftTrigger,
                gamepad.RightTrigger,
                gamepad.LeftThumbX,
                gamepad.LeftThumbY,
                gamepad.RightThumbX,
                gamepad.RightThumbY);
            return true;
        }
        catch (DllNotFoundException)
        {
            snapshot = ControllerSnapshot.Disconnected;
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            snapshot = ControllerSnapshot.Disconnected;
            return false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _safetyTimer.Dispose();
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint userIndex, out XInputState state);

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short LeftThumbX;
        public short LeftThumbY;
        public short RightThumbX;
        public short RightThumbY;
    }
}
