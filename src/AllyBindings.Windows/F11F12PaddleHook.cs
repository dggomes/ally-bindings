using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AllyBindings.Core;

namespace AllyBindings.Windows;

public sealed record RearPaddleKeyTransition(ControllerButton Paddle, bool IsDown);

public sealed class PaddleHookFaultEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;
}

/// <summary>
/// Long-lived product hook for global non-injected F12/F11 transitions.
/// Low-level keyboard hooks cannot attribute a source device, so all physical
/// F11/F12 keys are suppressed while this hook is active; injected events pass through.
/// </summary>
public sealed class F11F12PaddleHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint WmQuit = 0x0012;
    private const uint VkF11 = 0x7A;
    private const uint VkF12 = 0x7B;
    private const uint LlkLowerIntegrityInjected = 0x02;
    private const uint LlkInjected = 0x10;

    private readonly HookProcedure _callback;
    private readonly BlockingCollection<RearPaddleKeyTransition> _events = new(
        new ConcurrentQueue<RearPaddleKeyTransition>(), 128);
    private readonly ManualResetEventSlim _started = new(false);
    private Thread? _hookThread;
    private Task? _eventWorker;
    private IntPtr _hook;
    private uint _hookThreadId;
    private Exception? _failure;
    private int _stopping;
    private int _disposed;
    private bool _f11Down;
    private bool _f12Down;

    public F11F12PaddleHook() => _callback = HookCallback;

    public event EventHandler<RearPaddleKeyTransition>? PaddleStateChanged;
    public event EventHandler<PaddleHookFaultEventArgs>? Faulted;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_hookThread is not null) return;

        _eventWorker = Task.Run(ProcessEvents);
        _hookThread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = "AllyBindings.F11F12PaddleHook",
        };
        _hookThread.Start();
        if (!_started.Wait(TimeSpan.FromSeconds(5)))
        {
            Dispose();
            throw new TimeoutException("Timed out while starting the F11/F12 paddle hook.");
        }
        if (_failure is not null) throw new InvalidOperationException("The F11/F12 paddle hook could not start.", _failure);
    }

    private void RunMessageLoop()
    {
        try
        {
            _hookThreadId = GetCurrentThreadId();
            _ = PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            var module = GetModuleHandle(Process.GetCurrentProcess().MainModule?.ModuleName);
            _hook = SetWindowsHookEx(WhKeyboardLl, _callback, module, 0);
            if (_hook == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not install the F11/F12 paddle hook.");
            _started.Set();

            int result;
            while ((result = GetMessage(out var message, IntPtr.Zero, 0, 0)) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
            if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "The paddle-hook message loop failed.");
        }
        catch (Exception ex)
        {
            ReportFault(ex);
            _started.Set();
        }
        finally
        {
            Interlocked.Exchange(ref _stopping, 1);
            var hook = Interlocked.Exchange(ref _hook, IntPtr.Zero);
            if (hook != IntPtr.Zero)
            {
                if (!UnhookWindowsHookEx(hook))
                    ReportFault(new Win32Exception(Marshal.GetLastWin32Error(), "Could not remove the F11/F12 paddle hook."));
            }
            _events.CompleteAdding();
        }
    }

    private void ProcessEvents()
    {
        try
        {
            foreach (var transition in _events.GetConsumingEnumerable())
                PaddleStateChanged?.Invoke(this, transition);
        }
        catch (Exception ex)
        {
            ReportFault(ex);
            RequestStop();
        }
    }

    private IntPtr HookCallback(int code, UIntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return CallNextHookEx(_hook, code, wParam, lParam);
        try
        {
            var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            if (data.VirtualKey is not (VkF11 or VkF12))
                return CallNextHookEx(_hook, code, wParam, lParam);
            if ((data.Flags & (LlkInjected | LlkLowerIntegrityInjected)) != 0)
                return CallNextHookEx(_hook, code, wParam, lParam);

            var message = unchecked((uint)wParam.ToUInt64());
            var isDown = message is WmKeyDown or WmSysKeyDown;
            var isUp = message is WmKeyUp or WmSysKeyUp;
            if (!isDown && !isUp) return CallNextHookEx(_hook, code, wParam, lParam);

            ref var wasDown = ref data.VirtualKey == VkF11 ? ref _f11Down : ref _f12Down;
            if (wasDown != isDown)
            {
                wasDown = isDown;
                var paddle = data.VirtualKey == VkF12 ? ControllerButton.M1 : ControllerButton.M2;
                if (Volatile.Read(ref _stopping) == 0 && !_events.TryAdd(new(paddle, isDown)))
                {
                    ReportFault(new InvalidOperationException("The bounded paddle-event queue is full."));
                    RequestStop();
                }
            }

            return new IntPtr(1);
        }
        catch (Exception ex)
        {
            ReportFault(ex);
            RequestStop();
            return new IntPtr(1);
        }
    }

    private void ReportFault(Exception exception)
    {
        if (Interlocked.CompareExchange(ref _failure, exception, null) is null)
            Faulted?.Invoke(this, new PaddleHookFaultEventArgs(exception));
    }

    private void RequestStop()
    {
        Interlocked.Exchange(ref _stopping, 1);
        var threadId = Volatile.Read(ref _hookThreadId);
        if (threadId != 0) _ = PostThreadMessage(threadId, WmQuit, UIntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Remove suppression first. Even if the message loop or event consumer
        // is unhealthy, teardown must immediately fail open for F11/F12.
        var hook = Interlocked.Exchange(ref _hook, IntPtr.Zero);
        if (hook != IntPtr.Zero && !UnhookWindowsHookEx(hook))
        {
            Interlocked.CompareExchange(ref _hook, hook, IntPtr.Zero);
            ReportFault(new Win32Exception(Marshal.GetLastWin32Error(), "Could not remove the F11/F12 paddle hook during disposal."));
        }
        RequestStop();
        if (_hookThread is { IsAlive: true } && Thread.CurrentThread != _hookThread)
            _hookThread.Join(TimeSpan.FromSeconds(3));
        _events.CompleteAdding();
        var workerStopped = false;
        try { workerStopped = _eventWorker?.Wait(TimeSpan.FromSeconds(3)) ?? true; } catch { }
        if (_hookThread is { IsAlive: true } || !workerStopped)
        {
            // Do not dispose synchronization objects that a stalled worker may
            // still touch. The hook has already been removed above.
            return;
        }
        _events.Dispose();
        _started.Dispose();
    }

    private delegate IntPtr HookProcedure(int code, UIntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct KbdLlHookStruct { public uint VirtualKey; public uint ScanCode; public uint Flags; public uint Time; public nuint ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public IntPtr Window; public uint Id; public UIntPtr WParam; public IntPtr LParam; public uint Time; public int PointX; public int PointY; public uint Private; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int hookId, HookProcedure callback, IntPtr module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, UIntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, IntPtr window, uint minimum, uint maximum);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessage(out Message message, IntPtr window, uint minimum, uint maximum, uint removeMessage);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Message message);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostThreadMessage(uint threadId, uint message, UIntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr GetModuleHandle(string? moduleName);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
