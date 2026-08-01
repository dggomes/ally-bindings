using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AllyBindings.Core;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using CheckBox = System.Windows.Controls.CheckBox;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;

namespace AllyBindings.Windows;

public sealed record ControllerBindingDisplay(string Label, string Glyph, BindingRow Row);

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly ControllerUiInputRouter _uiInput = new();
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
    private BindingRow? _bindingPickerRow;
    private Button? _bindingPickerOrigin;
    private string _editingProfileName = string.Empty;
    private TaskCompletionSource<bool>? _dialogCompletion;

    public MainWindow(AppConfiguration configuration, BackendStatus backendStatus)
    {
        InitializeComponent();
        ButtonOptions = ControllerButtons.ShortcutButtons;
        Load(configuration);
        SetBackendStatus(backendStatus);
        DataContext = this;
        Closing += OnClosing;
        Activated += (_, _) => EnsureControllerFocus();
    }

    public ObservableCollection<ProfileEditor> Profiles { get; } = [];
    public ObservableCollection<ControllerBindingDisplay> LeftBindings { get; } = [];
    public ObservableCollection<ControllerBindingDisplay> RightBindings { get; } = [];
    public IReadOnlyList<ControllerButton> ButtonOptions { get; }
    public IReadOnlyList<string> OnScreenKeys { get; } =
        Enumerable.Range('A', 26).Select(value => ((char)value).ToString())
            .Concat(Enumerable.Range(0, 10).Select(value => value.ToString()))
            .ToArray();

    public ProfileEditor? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (_selectedProfile == value) return;
            _selectedProfile = value;
            RebuildBindingDisplays();
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
    public bool CanEnableAsusRearButtonMappings => ArmouryProtocolValidation.IsOperationApproved(isRecoveryReset: false);

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
            EnableAsusRearButtonMappings = CanEnableAsusRearButtonMappings && EnableAsusRearButtonMappings,
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
        RebuildBindingDisplays();
        ShortcutButton1 = configuration.Shortcut.Buttons.ElementAtOrDefault(0);
        ShortcutButton2 = configuration.Shortcut.Buttons.ElementAtOrDefault(1);
        HoldMilliseconds = configuration.Shortcut.HoldMilliseconds.ToString();
        CommitDelayMilliseconds = configuration.Shortcut.CommitDelayMilliseconds.ToString();
        RunAtStartup = configuration.RunAtStartup;
        CheckForUpdatesAutomatically = configuration.CheckForUpdatesAutomatically;
        IncludePrereleaseUpdates = configuration.IncludePrereleaseUpdates;
        _lastUpdateCheckUtc = configuration.LastUpdateCheckUtc;
        EnableAsusRearButtonMappings = CanEnableAsusRearButtonMappings && configuration.EnableAsusRearButtonMappings;
    }

    public void SetBackendStatus(BackendStatus status) => BackendStatusText.Text = $"Backend: {status.Name} · {status.Health}\n{status.Message}";

    public void SetControllerStatus(int? index)
    {
        ControllerStatusText.Text = index.HasValue ? $"Controller {index.Value + 1} connected" : "No XInput controller";
        ControllerDot.Fill = new SolidColorBrush(index.HasValue ? Color.FromRgb(98, 214, 167) : Color.FromRgb(236, 169, 77));
    }

    public void SetStatus(string message) => StatusText.Text = message;
    public void SetUpdateStatus(string message) => UpdateStatusText.Text = message;
    public void SetUpdateBusy(bool isBusy) => HeaderUpdateButton.IsEnabled = !isBusy;
    public void SetArmouryCaptureStatus(string message) => ArmouryCaptureStatusText.Text = message;
    public void SetArmouryCaptureBusy(bool isBusy) => ArmouryCaptureButton.IsEnabled = !isBusy;
    public void AllowClose() => _allowClose = true;

    public Task<bool> ShowControllerDialogAsync(
        string title,
        string message,
        bool allowCancel = true,
        string primaryLabel = "Continue",
        string secondaryLabel = "Cancel")
    {
        if (_dialogCompletion is not null)
        {
            throw new InvalidOperationException("Another Ally Bindings dialog is already open.");
        }
        _dialogCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ControllerDialogTitle.Text = title;
        ControllerDialogMessage.Text = message;
        ControllerDialogPrimaryButton.Content = $"A  {primaryLabel}";
        ControllerDialogSecondaryButton.Content = $"B  {secondaryLabel}";
        ControllerDialogSecondaryButton.Visibility = allowCancel ? Visibility.Visible : Visibility.Collapsed;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (!IsVisible) Show();
        Activate();
        ControllerDialogOverlay.Visibility = Visibility.Visible;
        Keyboard.Focus(ControllerDialogPrimaryButton);
        return _dialogCompletion.Task;
    }

    public void FocusControllerDefault()
    {
        if (!IsVisible) return;
        NavigationList.UpdateLayout();
        if (NavigationList.ItemContainerGenerator.ContainerFromIndex(WorkspaceTabs.SelectedIndex) is ListBoxItem item)
        {
            Keyboard.Focus(item);
        }
    }

    public void HandleControllerInput(ControllerSnapshot snapshot)
    {
        var commands = _uiInput.Process(snapshot);
        if (!IsVisible || !IsActive || commands.Count == 0) return;

        if (ControllerDialogOverlay.Visibility == Visibility.Visible)
        {
            foreach (var command in commands) HandleControllerDialogCommand(command);
            return;
        }
        if (BindingPickerOverlay.Visibility == Visibility.Visible)
        {
            foreach (var command in commands) HandleBindingPickerCommand(command);
            return;
        }
        if (NameKeyboardOverlay.Visibility == Visibility.Visible)
        {
            foreach (var command in commands) HandleNameKeyboardCommand(command);
            return;
        }

        foreach (var command in commands)
        {
            switch (command)
            {
                case ControllerUiCommand.MoveUp:
                    MoveControllerFocus(FocusNavigationDirection.Up, -1);
                    break;
                case ControllerUiCommand.MoveDown:
                    MoveControllerFocus(FocusNavigationDirection.Down, 1);
                    break;
                case ControllerUiCommand.MoveLeft:
                    MoveControllerFocus(FocusNavigationDirection.Left, -1);
                    break;
                case ControllerUiCommand.MoveRight:
                    MoveControllerFocus(FocusNavigationDirection.Right, 1);
                    break;
                case ControllerUiCommand.PreviousSection:
                    SelectSection(-1);
                    break;
                case ControllerUiCommand.NextSection:
                    SelectSection(1);
                    break;
                case ControllerUiCommand.Activate:
                    ActivateFocusedControl();
                    break;
                case ControllerUiCommand.Back:
                    NavigateBack();
                    break;
                case ControllerUiCommand.Save:
                    _ = ((App)Application.Current).SaveEditorAsync(this);
                    break;
                case ControllerUiCommand.Apply:
                    _ = ApplySelectedAsync();
                    break;
            }
        }
    }

    private void HandleControllerDialogCommand(ControllerUiCommand command)
    {
        switch (command)
        {
            case ControllerUiCommand.MoveLeft:
            case ControllerUiCommand.MoveUp:
                Keyboard.Focus(ControllerDialogSecondaryButton.Visibility == Visibility.Visible
                    ? ControllerDialogSecondaryButton
                    : ControllerDialogPrimaryButton);
                break;
            case ControllerUiCommand.MoveRight:
            case ControllerUiCommand.MoveDown:
                Keyboard.Focus(ControllerDialogPrimaryButton);
                break;
            case ControllerUiCommand.Activate:
                ActivateFocusedControl();
                break;
            case ControllerUiCommand.Back when ControllerDialogSecondaryButton.Visibility == Visibility.Visible:
                CompleteControllerDialog(false);
                break;
        }
    }

    private void HandleNameKeyboardCommand(ControllerUiCommand command)
    {
        switch (command)
        {
            case ControllerUiCommand.MoveUp:
                MoveControllerFocus(FocusNavigationDirection.Up, -1);
                break;
            case ControllerUiCommand.MoveDown:
                MoveControllerFocus(FocusNavigationDirection.Down, 1);
                break;
            case ControllerUiCommand.MoveLeft:
                MoveControllerFocus(FocusNavigationDirection.Left, -1);
                break;
            case ControllerUiCommand.MoveRight:
                MoveControllerFocus(FocusNavigationDirection.Right, 1);
                break;
            case ControllerUiCommand.Activate:
                ActivateFocusedControl();
                break;
            case ControllerUiCommand.Back:
                CloseNameKeyboard();
                break;
            case ControllerUiCommand.Save:
                DeleteNameCharacter();
                break;
            case ControllerUiCommand.Apply:
                AppendNameText(" ");
                break;
        }
    }

    private void HandleBindingPickerCommand(ControllerUiCommand command)
    {
        switch (command)
        {
            case ControllerUiCommand.MoveUp:
            case ControllerUiCommand.MoveLeft:
                MoveBindingPickerSelection(-1);
                break;
            case ControllerUiCommand.MoveDown:
            case ControllerUiCommand.MoveRight:
                MoveBindingPickerSelection(1);
                break;
            case ControllerUiCommand.Activate:
                ConfirmBindingPicker();
                break;
            case ControllerUiCommand.Back:
                CloseBindingPicker();
                break;
        }
    }

    private void MoveBindingPickerSelection(int delta)
    {
        if (BindingPickerList.Items.Count == 0) return;
        BindingPickerList.SelectedIndex = Math.Clamp(
            BindingPickerList.SelectedIndex + delta,
            0,
            BindingPickerList.Items.Count - 1);
        BindingPickerList.ScrollIntoView(BindingPickerList.SelectedItem);
    }

    private void MoveControllerFocus(FocusNavigationDirection direction, int comboDelta)
    {
        var combo = FindOpenComboBox() ?? FindAncestor<ComboBox>(Keyboard.FocusedElement as DependencyObject);
        if (combo is not null)
        {
            if (combo.IsDropDownOpen && combo.Items.Count > 0)
            {
                combo.SelectedIndex = Math.Clamp(combo.SelectedIndex + comboDelta, 0, combo.Items.Count - 1);
                return;
            }
        }

        var focused = Keyboard.FocusedElement as UIElement;
        if (focused is null)
        {
            FocusControllerDefault();
            return;
        }
        focused.MoveFocus(new TraversalRequest(direction) { Wrapped = true });
    }

    private void ActivateFocusedControl()
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        if (FindOpenComboBox() is { } openCombo)
        {
            openCombo.IsDropDownOpen = false;
            Keyboard.Focus(openCombo);
            return;
        }
        if (FindAncestor<ComboBox>(focused) is { } combo)
        {
            combo.IsDropDownOpen = !combo.IsDropDownOpen;
            return;
        }
        if (FindAncestor<CheckBox>(focused) is { } checkBox)
        {
            checkBox.IsChecked = !(checkBox.IsChecked ?? false);
            return;
        }
        if (FindAncestor<Button>(focused) is { } button && button.IsEnabled)
        {
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            return;
        }
        if (FindAncestor<ListBoxItem>(focused) is { } item)
        {
            item.IsSelected = true;
        }
    }

    private void NavigateBack()
    {
        if (FindOpenComboBox() is { } combo)
        {
            combo.IsDropDownOpen = false;
            return;
        }
        if (WorkspaceTabs.SelectedIndex != 0)
        {
            WorkspaceTabs.SelectedIndex = 0;
        }
        FocusControllerDefault();
    }

    private void SelectSection(int delta)
    {
        var count = WorkspaceTabs.Items.Count;
        WorkspaceTabs.SelectedIndex = (WorkspaceTabs.SelectedIndex + delta + count) % count;
        FocusControllerDefault();
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = current is FrameworkContentElement content
                ? content.Parent
                : VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private ComboBox? FindOpenComboBox() =>
        FindVisualDescendants<ComboBox>(this).FirstOrDefault(combo => combo.IsDropDownOpen);

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualDescendants<T>(child)) yield return descendant;
        }
    }

    private void EnsureControllerFocus()
    {
        if (Keyboard.FocusedElement is null)
        {
            FocusControllerDefault();
        }
    }

    private void RebuildBindingDisplays()
    {
        LeftBindings.Clear();
        RightBindings.Clear();
        if (SelectedProfile is null) return;

        AddDisplay(LeftBindings, ControllerButton.LeftBumper, "Left bumper", "LB");
        AddDisplay(LeftBindings, ControllerButton.LeftStick, "Left stick click", "LS");
        AddDisplay(LeftBindings, ControllerButton.View, "View", "◧");
        AddDisplay(LeftBindings, ControllerButton.DPadUp, "D-pad up", "↑");
        AddDisplay(LeftBindings, ControllerButton.DPadLeft, "D-pad left", "←");
        AddDisplay(LeftBindings, ControllerButton.DPadRight, "D-pad right", "→");
        AddDisplay(LeftBindings, ControllerButton.DPadDown, "D-pad down", "↓");
        AddDisplay(LeftBindings, ControllerButton.M1, "Rear button M1", "M1");

        AddDisplay(RightBindings, ControllerButton.RightBumper, "Right bumper", "RB");
        AddDisplay(RightBindings, ControllerButton.RightStick, "Right stick click", "RS");
        AddDisplay(RightBindings, ControllerButton.Menu, "Menu", "☰");
        AddDisplay(RightBindings, ControllerButton.Y, "Y button", "Y");
        AddDisplay(RightBindings, ControllerButton.X, "X button", "X");
        AddDisplay(RightBindings, ControllerButton.B, "B button", "B");
        AddDisplay(RightBindings, ControllerButton.A, "A button", "A");
        AddDisplay(RightBindings, ControllerButton.M2, "Rear button M2", "M2");
    }

    private void AddDisplay(ICollection<ControllerBindingDisplay> destination, ControllerButton source, string label, string glyph)
    {
        var row = SelectedProfile?.Bindings.FirstOrDefault(candidate => candidate.Source == source);
        if (row is not null) destination.Add(new ControllerBindingDisplay(label, glyph, row));
    }

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        var suffix = 1;
        while (Profiles.Any(profile =>
                   string.Equals(profile.Id, $"new-profile-{suffix}", StringComparison.Ordinal) ||
                   string.Equals(ConfigurationValidator.Slugify(profile.Name), $"new-profile-{suffix}", StringComparison.Ordinal)))
        {
            suffix++;
        }
        var profile = new ProfileEditor(new MappingProfile { Id = $"new-profile-{suffix}", Name = $"New profile {suffix}" });
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

    private void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditSelected || SelectedProfile is null) return;
        _editingProfileName = SelectedProfile.Name;
        NameKeyboardPreview.Text = _editingProfileName;
        NameKeyboardOverlay.Visibility = Visibility.Visible;
        NameKeyboardKeys.UpdateLayout();
        if (NameKeyboardKeys.ItemContainerGenerator.ContainerFromIndex(0) is ContentPresenter presenter &&
            FindVisualDescendants<Button>(presenter).FirstOrDefault() is { } firstKey)
        {
            Keyboard.Focus(firstKey);
        }
    }

    private void NameKeyboardKey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string key }) AppendNameText(key);
    }

    private void NameKeyboardSpace_Click(object sender, RoutedEventArgs e) => AppendNameText(" ");
    private void NameKeyboardDelete_Click(object sender, RoutedEventArgs e) => DeleteNameCharacter();
    private void NameKeyboardClear_Click(object sender, RoutedEventArgs e)
    {
        _editingProfileName = string.Empty;
        NameKeyboardPreview.Text = string.Empty;
    }
    private void NameKeyboardCancel_Click(object sender, RoutedEventArgs e) => CloseNameKeyboard();
    private void NameKeyboardDone_Click(object sender, RoutedEventArgs e) => CommitNameKeyboard();

    private void AppendNameText(string text)
    {
        if (_editingProfileName.Length >= 48) return;
        if (text.Length == 1 && char.IsLetter(text[0]))
        {
            text = _editingProfileName.Length == 0 || char.IsWhiteSpace(_editingProfileName[^1])
                ? text.ToUpperInvariant()
                : text.ToLowerInvariant();
        }
        _editingProfileName += text;
        NameKeyboardPreview.Text = _editingProfileName;
    }

    private void DeleteNameCharacter()
    {
        if (_editingProfileName.Length == 0) return;
        _editingProfileName = _editingProfileName[..^1];
        NameKeyboardPreview.Text = _editingProfileName;
    }

    private void CommitNameKeyboard()
    {
        var candidate = _editingProfileName.Trim();
        if (candidate.Length == 0)
        {
            SetStatus("Profile name cannot be empty.");
            return;
        }
        var candidateId = ConfigurationValidator.Slugify(candidate);
        if (Profiles.Any(profile =>
                profile != SelectedProfile &&
                string.Equals(ConfigurationValidator.Slugify(profile.Name), candidateId, StringComparison.Ordinal)))
        {
            SetStatus("That profile name is already in use.");
            return;
        }
        if (SelectedProfile is not null) SelectedProfile.Name = candidate;
        CloseNameKeyboard();
    }

    private void CloseNameKeyboard()
    {
        NameKeyboardOverlay.Visibility = Visibility.Collapsed;
        _editingProfileName = string.Empty;
        FocusControllerDefault();
    }

    private void DecreaseHold_Click(object sender, RoutedEventArgs e) => AdjustTiming(isHold: true, -50, 100, 2000);
    private void IncreaseHold_Click(object sender, RoutedEventArgs e) => AdjustTiming(isHold: true, 50, 100, 2000);
    private void DecreaseCommitDelay_Click(object sender, RoutedEventArgs e) => AdjustTiming(isHold: false, -100, 300, 5000);
    private void IncreaseCommitDelay_Click(object sender, RoutedEventArgs e) => AdjustTiming(isHold: false, 100, 300, 5000);

    private void AdjustTiming(bool isHold, int delta, int minimum, int maximum)
    {
        var raw = isHold ? HoldMilliseconds : CommitDelayMilliseconds;
        if (!int.TryParse(raw, out var value)) value = minimum;
        var updated = Math.Clamp(value + delta, minimum, maximum).ToString();
        if (isHold) HoldMilliseconds = updated;
        else CommitDelayMilliseconds = updated;
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await ((App)Application.Current).SaveEditorAsync(this);
    private async void Apply_Click(object sender, RoutedEventArgs e) => await ApplySelectedAsync();

    private async Task ApplySelectedAsync()
    {
        if (SelectedProfile is null) return;
        var profileId = SelectedProfile.IsDefault ? MappingProfile.Default.Id : ConfigurationValidator.Slugify(SelectedProfile.Name);
        var saved = await ((App)Application.Current).SaveEditorAsync(this);
        if (saved) await ((App)Application.Current).ApplyProfileAsync(profileId, showOverlay: true);
    }

    private async void Panic_Click(object sender, RoutedEventArgs e) => await ((App)Application.Current).RestoreDefaultAsync("Main-window reset");
    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e) => await ((App)Application.Current).CheckForUpdatesAsync(userInitiated: true);
    private async void CaptureArmouryProtocol_Click(object sender, RoutedEventArgs e) => await ((App)Application.Current).CaptureArmouryProtocolAsync();

    private void OpenControllerPage_Click(object sender, RoutedEventArgs e)
    {
        WorkspaceTabs.SelectedIndex = 1;
        FocusControllerDefault();
    }

    private void ResetBindings_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEditSelected || SelectedProfile is null) return;
        foreach (var row in SelectedProfile.Bindings) row.Target = row.Source;
        SetStatus("Layout reset locally. Press X or Save to keep it.");
    }

    private void BindingTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BindingRow row } button || !CanEditSelected) return;
        _bindingPickerRow = row;
        _bindingPickerOrigin = button;
        var label = button.DataContext is ControllerBindingDisplay display ? display.Label : row.Source.ToString();
        BindingPickerTitle.Text = $"Map {label}";
        BindingPickerList.ItemsSource = row.TargetOptions;
        BindingPickerList.SelectedItem = row.Target;
        BindingPickerOverlay.Visibility = Visibility.Visible;
        BindingPickerList.UpdateLayout();
        if (BindingPickerList.ItemContainerGenerator.ContainerFromItem(row.Target) is ListBoxItem item)
        {
            Keyboard.Focus(item);
        }
        else
        {
            Keyboard.Focus(BindingPickerList);
        }
    }

    private void ConfirmBindingPicker_Click(object sender, RoutedEventArgs e) => ConfirmBindingPicker();
    private void CancelBindingPicker_Click(object sender, RoutedEventArgs e) => CloseBindingPicker();

    private void ConfirmBindingPicker()
    {
        if (_bindingPickerRow is not null && BindingPickerList.SelectedItem is ControllerButton target)
        {
            _bindingPickerRow.Target = target;
            SetStatus($"Mapped {_bindingPickerRow.Source} to {target}. Press X or Save to keep it.");
        }
        CloseBindingPicker();
    }

    private void CloseBindingPicker()
    {
        BindingPickerOverlay.Visibility = Visibility.Collapsed;
        BindingPickerList.ItemsSource = null;
        _bindingPickerRow = null;
        var origin = _bindingPickerOrigin;
        _bindingPickerOrigin = null;
        if (origin is { IsVisible: true, IsEnabled: true }) Keyboard.Focus(origin);
    }

    private void ControllerDialogPrimary_Click(object sender, RoutedEventArgs e) => CompleteControllerDialog(true);
    private void ControllerDialogSecondary_Click(object sender, RoutedEventArgs e) => CompleteControllerDialog(false);

    private void CompleteControllerDialog(bool result)
    {
        if (_dialogCompletion is null) return;
        var completion = _dialogCompletion;
        _dialogCompletion = null;
        ControllerDialogOverlay.Visibility = Visibility.Collapsed;
        completion.TrySetResult(result);
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(((App)Application.Current).BuildDiagnostics());
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
            : "View + Menu avoids common gameplay actions. The chord is observed, not swallowed, in preview mode.";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
