using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AllyBindings.SoftwareProbe;

namespace AllyBindings.M1M2Probe;

internal sealed class F11F12KeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint WmQuit = 0x0012;
    private const uint VkF11 = 0x7A;
    private const uint VkF12 = 0x7B;
    private const uint LlkInjected = 0x10;

    private readonly bool _suppress;
    private readonly string _mode;
    private readonly Action<SoftwareProbeKeyEvent> _eventSink;
    private readonly Action<string, bool, bool>? _keyStateSink;
    private readonly HookProcedure _callback;
    private readonly BlockingCollection<SoftwareProbeKeyEvent> _events = new(
        new ConcurrentQueue<SoftwareProbeKeyEvent>(),
        boundedCapacity: 256);
    private IntPtr _hook;
    private Exception? _failure;
    private uint _messageThreadId;
    private int _stopping;
    private bool _f11Down;
    private bool _f12Down;
    private bool _disposed;

    internal F11F12KeyboardHook(
        bool suppress,
        string mode,
        Action<SoftwareProbeKeyEvent> eventSink,
        Action<string, bool, bool>? keyStateSink = null)
    {
        _suppress = suppress;
        _mode = mode;
        _eventSink = eventSink;
        _keyStateSink = keyStateSink;
        _callback = HookCallback;
    }

    internal void Run(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        WindowsCapabilities.EnsureWindows();
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(30))
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be between one second and thirty minutes.");

        _messageThreadId = GetCurrentThreadId();
        _ = PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        var worker = Task.Run(ProcessEvents);
        Exception? loopFailure = null;

        try
        {
            var module = GetModuleHandle(Process.GetCurrentProcess().MainModule?.ModuleName);
            _hook = SetWindowsHookEx(WhKeyboardLl, _callback, module, 0);
            if (_hook == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not install the F11/F12 keyboard hook.");

            using var cancellation = cancellationToken.Register(RequestStop);
            using var timer = new Timer(_ => RequestStop(), null, duration, Timeout.InfiniteTimeSpan);
            int messageResult;
            while ((messageResult = GetMessage(out var message, IntPtr.Zero, 0, 0)) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
            if (messageResult < 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The F11/F12 message loop failed.");
        }
        catch (Exception exception)
        {
            loopFailure = exception;
        }
        finally
        {
            Interlocked.Exchange(ref _stopping, 1);
            if (_hook != IntPtr.Zero)
            {
                if (!UnhookWindowsHookEx(_hook))
                    SetFailure(new Win32Exception(Marshal.GetLastWin32Error(), "Could not remove the F11/F12 keyboard hook."));
                _hook = IntPtr.Zero;
            }
            _events.CompleteAdding();
            try
            {
                worker.GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                SetFailure(exception);
            }
        }

        var failures = new List<Exception>();
        if (loopFailure is not null) failures.Add(loopFailure);
        if (_failure is not null) failures.Add(_failure);
        if (failures.Count != 0)
            throw new AggregateException("The F11/F12 hook stopped after a failure.", failures);
    }


    private void ProcessEvents()
    {
        try
        {
            foreach (var keyEvent in _events.GetConsumingEnumerable())
            {
                _keyStateSink?.Invoke(keyEvent.Key, keyEvent.IsKeyDown, keyEvent.IsInjected);
                _eventSink(keyEvent);
            }
        }
        catch (Exception exception)
        {
            SetFailure(exception);
            Interlocked.Exchange(ref _stopping, 1);
            RequestStop();
        }
    }

    private IntPtr HookCallback(int code, UIntPtr wParam, IntPtr lParam)
    {
        if (code < 0)
            return CallNextHookEx(_hook, code, wParam, lParam);

        try
        {
            var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            var key = data.VirtualKey switch
            {
                VkF11 => "F11",
                VkF12 => "F12",
                _ => null,
            };
            if (key is null)
                return CallNextHookEx(_hook, code, wParam, lParam);

            var injected = (data.Flags & LlkInjected) != 0;
            if (injected)
                return CallNextHookEx(_hook, code, wParam, lParam);

            var message = unchecked((uint)wParam.ToUInt64());
            var isDown = message is WmKeyDown or WmSysKeyDown;
            var isUp = message is WmKeyUp or WmSysKeyUp;
            if (!isDown && !isUp)
                return CallNextHookEx(_hook, code, wParam, lParam);

            ref var wasDown = ref data.VirtualKey == VkF11 ? ref _f11Down : ref _f12Down;
            if (wasDown == isDown)
                return _suppress ? new IntPtr(1) : CallNextHookEx(_hook, code, wParam, lParam);
            wasDown = isDown;

            if (Volatile.Read(ref _stopping) == 0 && !_events.TryAdd(new(
                    DateTimeOffset.UtcNow,
                    key,
                    IsKeyDown: isDown,
                    IsInjected: false,
                    WasSuppressed: _suppress,
                    Mode: _mode)))
            {
                SetFailure(new InvalidOperationException("The bounded F11/F12 event queue is full."));
                Interlocked.Exchange(ref _stopping, 1);
                RequestStop();
            }
            return _suppress ? new IntPtr(1) : CallNextHookEx(_hook, code, wParam, lParam);
        }
        catch (Exception exception)
        {
            SetFailure(exception);
            Interlocked.Exchange(ref _stopping, 1);
            RequestStop();
            return _suppress ? new IntPtr(1) : CallNextHookEx(_hook, code, wParam, lParam);
        }
    }

    private void RequestStop()
    {
        if (_messageThreadId == 0) return;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (PostThreadMessage(_messageThreadId, WmQuit, UIntPtr.Zero, IntPtr.Zero)) return;
            Thread.Sleep(5);
        }
        SetFailure(new Win32Exception(Marshal.GetLastWin32Error(), "Could not stop the F11/F12 message loop."));
    }

    private void SetFailure(Exception exception) => Interlocked.CompareExchange(ref _failure, exception, null);


    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Exchange(ref _stopping, 1);
        RequestStop();
        if (_hook != IntPtr.Zero)
        {
            _ = UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        _events.Dispose();
    }

    private delegate IntPtr HookProcedure(int code, UIntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }


    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint Id;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
        public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookId, HookProcedure callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Message message, IntPtr window, uint minimum, uint maximum);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Message message, IntPtr window, uint minimum, uint maximum, uint removeMessage);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Message message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);


    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
