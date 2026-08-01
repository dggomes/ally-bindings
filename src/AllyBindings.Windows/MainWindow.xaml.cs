using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using AllyBindings.Core;

namespace AllyBindings.Windows;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private ProfileEditor? _selectedProfile;
    private ControllerButton _shortcutButton1;
    private ControllerButton _shortcutButton2;
    private string _holdMilliseconds = "250";
    private string _commitDelayMilliseconds = "900";
    private bool _runAtStartup;
    private bool _checkForUpdatesAutomatically;
    private bool _includePrereleaseUpdates = true;
    private DateTimeOffset? _lastUpdateCheckUtc;
    private bool _enableAsusRearButtonMappings;
    private bool _allowClose;

    public MainWindow(AppConfiguration configuration, BackendStatus backendStatus)
    {
        InitializeComponent();
        ButtonOptions = ControllerButtons.ShortcutButtons;
        Load(configuration);
        SetBackendStatus(backendStatus);
        DataContext = this;
        Closing += OnClosing;
    }

    public ObservableCollection<ProfileEditor> Profiles { get; } = [];
    public IReadOnlyList<ControllerButton> ButtonOptions { get; }

    public ProfileEditor? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (_selectedProfile == value) return;
            _selectedProfile = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanEditSelected));
        }
    }

    public bool CanEditSelected => SelectedProfile is { IsDefault: false };
    public ControllerButton ShortcutButton1 { get => _shortcutButton1; set { _shortcutButton1 = value; OnPropertyChanged(); UpdateShortcutWarning(); } }
    public ControllerButton ShortcutButton2 { get => _shortcutButton2; set { _shortcutButton2 = value; OnPropertyChanged(); UpdateShortcutWarning(); } }
    public string HoldMilliseconds { get => _holdMilliseconds; set { _holdMilliseconds = value; OnPropertyChanged(); } }
    public string CommitDelayMilliseconds { get => _commitDelayMilliseconds; set { _commitDelayMilliseconds = value; OnPropertyChanged(); } }
    public bool RunAtStartup { get => _runAtStartup; set { _runAtStartup = value; OnPropertyChanged(); } }
    public bool CheckForUpdatesAutomatically { get => _checkForUpdatesAutomatically; set { _checkForUpdatesAutomatically = value; OnPropertyChanged(); } }
    public bool IncludePrereleaseUpdates { get => _includePrereleaseUpdates; set { _includePrereleaseUpdates = value; OnPropertyChanged(); } }
    public bool EnableAsusRearButtonMappings { get => _enableAsusRearButtonMappings; set { _enableAsusRearButtonMappings = value; OnPropertyChanged(); } }

    public AppConfiguration BuildConfiguration(string activeProfileId, int? controllerIndex)
    {
        var hold = ParseTiming(HoldMilliseconds, 100, 2000, "Hold duration");
        var delay = ParseTiming(CommitDelayMilliseconds, 300, 5000, "Commit delay");
        return new AppConfiguration
        {
            ActiveProfileId = activeProfileId,
            ControllerIndex = controllerIndex,
            RunAtStartup = RunAtStartup,
            CheckForUpdatesAutomatically = CheckForUpdatesAutomatically,
            IncludePrereleaseUpdates = IncludePrereleaseUpdates,
            LastUpdateCheckUtc = _lastUpdateCheckUtc,
            EnableAsusRearButtonMappings = EnableAsusRearButtonMappings,
            Shortcut = new ShortcutSettings
            {
                Buttons = [ShortcutButton1, ShortcutButton2],
                HoldMilliseconds = hold,
                CommitDelayMilliseconds = delay,
            },
            Profiles = Profiles.Select(profile => profile.ToProfile()).ToList(),
        };
    }

    private static int ParseTiming(string value, int minimum, int maximum, string label)
    {
        if (!int.TryParse(value, out var parsed) || parsed < minimum || parsed > maximum)
        {
            throw new InvalidOperationException($"{label} must be a whole number between {minimum} and {maximum} milliseconds.");
        }
        return parsed;
    }

    public void Load(AppConfiguration configuration, string? preferredSelectionId = null)
    {
        Profiles.Clear();
        foreach (var profile in configuration.Profiles)
        {
            Profiles.Add(new ProfileEditor(profile));
        }
        var selectionId = preferredSelectionId ?? configuration.ActiveProfileId;
        SelectedProfile = Profiles.FirstOrDefault(profile => profile.Id == selectionId)
            ?? Profiles.FirstOrDefault(profile => profile.Id == configuration.ActiveProfileId)
            ?? Profiles.FirstOrDefault();
        ShortcutButton1 = configuration.Shortcut.Buttons.ElementAtOrDefault(0);
        ShortcutButton2 = configuration.Shortcut.Buttons.ElementAtOrDefault(1);
        HoldMilliseconds = configuration.Shortcut.HoldMilliseconds.ToString();
        CommitDelayMilliseconds = configuration.Shortcut.CommitDelayMilliseconds.ToString();
        RunAtStartup = configuration.RunAtStartup;
        CheckForUpdatesAutomatically = configuration.CheckForUpdatesAutomatically;
        IncludePrereleaseUpdates = configuration.IncludePrereleaseUpdates;
        _lastUpdateCheckUtc = configuration.LastUpdateCheckUtc;
        EnableAsusRearButtonMappings = configuration.EnableAsusRearButtonMappings;
    }

    public void SetBackendStatus(BackendStatus status) => BackendStatusText.Text = $"Backend: {status.Name} · {status.Health}\n{status.Message}";

    public void SetControllerStatus(int? index)
    {
        ControllerStatusText.Text = index.HasValue ? $"Controller {index.Value + 1} connected" : "No XInput controller";
        ControllerDot.Fill = new SolidColorBrush(index.HasValue ? System.Windows.Media.Color.FromRgb(79, 201, 134) : System.Windows.Media.Color.FromRgb(236, 169, 77));
    }

    public void SetStatus(string message) => StatusText.Text = message;

    public void SetUpdateStatus(string message) => UpdateStatusText.Text = message;

    public void AllowClose() => _allowClose = true;

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        var id = $"profile-{Profiles.Count}";
        var profile = new ProfileEditor(new MappingProfile { Id = id, Name = $"New profile {Profiles.Count}" });
        Profiles.Add(profile);
        SelectedProfile = profile;
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null || SelectedProfile.IsDefault) return;
        var selectedIndex = Profiles.IndexOf(SelectedProfile);
        Profiles.Remove(SelectedProfile);
        SelectedProfile = Profiles[Math.Clamp(selectedIndex - 1, 0, Profiles.Count - 1)];
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await ((App)System.Windows.Application.Current).SaveEditorAsync(this);

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedProfile is null) return;
        var profileId = SelectedProfile.IsDefault
            ? MappingProfile.Default.Id
            : ConfigurationValidator.Slugify(SelectedProfile.Name);
        var saved = await ((App)System.Windows.Application.Current).SaveEditorAsync(this);
        if (!saved) return;
        await ((App)System.Windows.Application.Current).ApplyProfileAsync(profileId, showOverlay: true);
    }

    private async void Panic_Click(object sender, RoutedEventArgs e) => await ((App)System.Windows.Application.Current).RestoreDefaultAsync("Main-window reset");

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e) =>
        await ((App)System.Windows.Application.Current).CheckForUpdatesAsync(userInitiated: true);

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(((App)System.Windows.Application.Current).BuildDiagnostics());
            SetStatus("Diagnostics copied. No input history or secrets included.");
        }
        catch (Exception ex)
        {
            SetStatus($"Clipboard is unavailable; try again: {ex.Message}");
        }
    }

    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        Hide();
    }

    private void UpdateShortcutWarning()
    {
        if (ShortcutWarningText is null) return;
        var face = new[] { ControllerButton.A, ControllerButton.B, ControllerButton.X, ControllerButton.Y };
        ShortcutWarningText.Text = face.Contains(ShortcutButton1) && face.Contains(ShortcutButton2)
            ? "Warning: face-button chords can leak into the streamed game unless a validated interception backend is active."
            : "Default View + Menu avoids common gameplay actions. The chord is observed, not swallowed, in preview mode.";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
