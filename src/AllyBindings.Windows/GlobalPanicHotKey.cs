using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace AllyBindings.Windows;

public sealed class GlobalPanicHotKey : IDisposable
{
    private const int HotKeyId = 0xA11B;
    private const int WmHotKey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint VkF12 = 0x7B;
    private readonly HwndSource _source;
    private bool _registered;

    public GlobalPanicHotKey()
    {
        var parameters = new HwndSourceParameters("AllyBindingsHotKey")
        {
            ParentWindow = new IntPtr(-3),
            WindowStyle = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WindowProc);
        _registered = RegisterHotKey(_source.Handle, HotKeyId, ModControl | ModAlt, VkF12);
    }

    public event EventHandler? Pressed;
    public bool IsRegistered => _registered;

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotKey && wParam.ToInt32() == HotKeyId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_registered)
        {
            UnregisterHotKey(_source.Handle, HotKeyId);
            _registered = false;
        }
        _source.RemoveHook(WindowProc);
        _source.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
