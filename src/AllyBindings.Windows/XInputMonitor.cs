using System.Runtime.InteropServices;
using System.Windows.Threading;
using AllyBindings.Core;

namespace AllyBindings.Windows;

public sealed class XInputMonitor : IDisposable
{
    private readonly DispatcherTimer _timer;
    private int? _preferredIndex;
    private bool _disposed;

    public XInputMonitor(int? preferredIndex)
    {
        _preferredIndex = preferredIndex;
        _timer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(20),
        };
        _timer.Tick += (_, _) => Poll();
    }

    public event EventHandler<ControllerSnapshot>? SnapshotReceived;
    public event EventHandler<int?>? ActiveControllerChanged;

    public int? ActiveControllerIndex { get; private set; }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    public void SetPreferredIndex(int? index)
    {
        _preferredIndex = index is >= 0 and <= 3 ? index : null;
        ActiveControllerIndex = null;
    }

    private void Poll()
    {
        if (_disposed)
        {
            return;
        }

        var indices = _preferredIndex.HasValue
            ? new[] { _preferredIndex.Value }
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
