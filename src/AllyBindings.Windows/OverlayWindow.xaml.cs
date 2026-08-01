using System.Windows;
using System.Windows.Threading;

namespace AllyBindings.Windows;

public partial class OverlayWindow : Window
{
    private readonly DispatcherTimer _hideTimer;

    public OverlayWindow()
    {
        InitializeComponent();
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            Hide();
        };
    }

    public void ShowSelection(string label, string detail = "Release and repeat to rotate · keep holding and press RT to open")
    {
        _hideTimer.Stop();
        EyebrowText.Text = "SELECT PROFILE";
        SelectionText.Text = label;
        DetailText.Text = detail;
        Position();
        if (!IsVisible) Show();
    }

    public void ShowResult(string label, string detail)
    {
        EyebrowText.Text = "ALLY BINDINGS";
        SelectionText.Text = label;
        DetailText.Text = detail;
        Position();
        if (!IsVisible) Show();
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    public void Dismiss()
    {
        _hideTimer.Stop();
        Hide();
    }

    private void Position()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Bottom - Height - 72;
    }
}
