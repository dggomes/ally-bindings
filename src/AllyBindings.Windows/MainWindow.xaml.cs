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
using Point = System.Windows.Point;

namespace AllyBindings.Windows;

public sealed record ControllerBindingDisplay(
    string Label,
    string Glyph,
    string AutomationId,
    BindingRow Row);

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private static readonly (ControllerButton Source, Point Center)[] DiagramControlCenters =
    [
        (ControllerButton.LeftTrigger, new Point(157.5, 81)),
        (ControllerButton.RightTrigger, new Point(542.5, 81)),
        (ControllerButton.LeftBumper, new Point(175, 106.5)),
        (ControllerButton.RightBumper, new Point(525, 106.5)),
        (ControllerButton.LeftStick, new Point(151, 176)),
        (ControllerButton.DPadUp, new Point(164.5, 244)),
        (ControllerButton.DPadLeft, new Point(139, 268.5)),
        (ControllerButton.DPadRight, new Point(191.5, 268.5)),
        (ControllerButton.DPadDown, new Point(164.5, 294.5)),
        (ControllerButton.View, new Point(222, 338)),
        (ControllerButton.Menu, new Point(478, 338)),
        (ControllerButton.Y, new Point(551, 155)),
        (ControllerButton.X, new Point(506, 200)),
        (ControllerButton.B, new Point(596, 200)),
        (ControllerButton.A, new Point(551, 245)),
        (ControllerButton.RightStick, new Point(546, 315)),
        (ControllerButton.M1, new Point(164, 405)),
        (ControllerButton.M2, new Point(536, 405)),
    ];

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
    private bool _enableVirtualControllerRemapping;
    private bool _allowClose;
    private BindingRow? _bindingPickerRow;
    private Button? _bindingPickerOrigin;
    private string _editingProfileName = string.Empty;
    private bool _workspaceInteractive = true;
    private bool _updateBusy;
    private bool _armouryCaptureBlocked;
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private TaskCompletionSource<bool>? _dialogCompletion;
    private Point? _diagramMouseDownPosition;
    private readonly Dictionary<int, Point> _diagramTouchDownPositions = [];

    public MainWindow(AppConfiguration configuration, BackendStatus backendStatus)
    {
        InitializeComponent();
        ApplyResponsiveInitialSize();
        ButtonOptions = ControllerButtons.ShortcutButtons;
        Load(configuration);
        SetBackendStatus(backendStatus);
        DataContext = this;
        Closing += OnClosing;
        Activated += (_, _) => EnsureControllerFocus();
    }

    private void ApplyResponsiveInitialSize()
    {
        var workArea = SystemParameters.WorkArea;
        var compactLandscape = workArea.Width <= 1200;
        Width = Math.Clamp(workArea.Width * (compactLandscape ? 1.0 : 0.88), MinWidth, 1600);
        Height = Math.Clamp(workArea.Height * (compactLandscape ? 1.0 : 0.88), MinHeight, 920);
    }

    public ObservableCollection<ProfileEditor> Profiles { get; } = [];
    public ObservableCollection<ControllerBindingDisplay> LeftBindings { get; } = [];
    public ObservableCollection<ControllerBindingDisplay> DPadBindings { get; } = [];
    public ObservableCollection<ControllerBindingDisplay> FaceBindings { get; } = [];
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
    public bool EnableVirtualControllerRemapping { get => _enableVirtualControllerRemapping; set { _enableVirtualControllerRemapping = value; OnPropertyChanged(); } }
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
            EnableVirtualControllerRemapping = EnableVirtualControllerRemapping,
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
        EnableVirtualControllerRemapping = configuration.EnableVirtualControllerRemapping;
    }

    public void SetBackendStatus(BackendStatus status) => BackendStatusText.Text = $"Backend: {status.Name} · {status.Health}\n{status.Message}";

    public void SetControllerStatus(int? index)
    {
        ControllerStatusText.Text = index.HasValue ? $"Controller {index.Value + 1} connected" : "No XInput controller";
        ControllerDot.Fill = new SolidColorBrush(index.HasValue ? Color.FromRgb(98, 214, 167) : Color.FromRgb(236, 169, 77));
    }

    public void SetStatus(string message) => StatusText.Text = message;
    public void SetUpdateStatus(string message) => UpdateStatusText.Text = message;
    public void SetUpdateBusy(bool isBusy)
    {
        _updateBusy = isBusy;
        HeaderUpdateButton.IsEnabled = _workspaceInteractive && !_updateBusy;
    }
    public void SetArmouryCaptureStatus(string message) => ArmouryCaptureStatusText.Text = message;
    public void SetRearButtonSnapshotStatus(string message) => RearButtonSnapshotStatusText.Text = message;
    public void SetArmouryCaptureBusy(bool isBusy)
    {
        ArmouryCaptureButton.IsEnabled = !isBusy && !_armouryCaptureBlocked;
        RearButtonSnapshotButton.IsEnabled = !isBusy && !_armouryCaptureBlocked;
    }
    public void SetArmouryCaptureBlocked(bool isBlocked, string? message = null)
    {
        _armouryCaptureBlocked = isBlocked;
        ArmouryCaptureButton.IsEnabled = !isBlocked;
        RearButtonSnapshotButton.IsEnabled = !isBlocked;
        ArmouryCaptureButton.Content = isBlocked ? "Restart Windows" : "Start capture";
        if (!string.IsNullOrWhiteSpace(message))
        {
            SetArmouryCaptureStatus(message);
            SetRearButtonSnapshotStatus(message);
        }
    }
    public void AllowClose() => _allowClose = true;
    public void CancelControllerDialog() => CompleteControllerDialog(false);

    public async Task<bool> ShowControllerDialogAsync(
        string title,
        string message,
        bool allowCancel = true,
        string primaryLabel = "Continue",
        string secondaryLabel = "Cancel")
    {
        await _dialogGate.WaitAsync();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IInputElement? dialogOrigin = null;
        try
        {
            if (BindingPickerOverlay.Visibility == Visibility.Visible) CloseBindingPicker();
            if (NameKeyboardOverlay.Visibility == Visibility.Visible) CloseNameKeyboard();
            dialogOrigin = Keyboard.FocusedElement;
            SetWorkspaceInteractive(false);
            _dialogCompletion = completion;
            ControllerDialogTitle.Text = title;
            ControllerDialogMessage.Text = message;
            ControllerDialogPrimaryButton.Content = $"A  {primaryLabel}";
            ControllerDialogSecondaryButton.Content = $"B  {secondaryLabel}";
            ControllerDialogSecondaryButton.Visibility = allowCancel ? Visibility.Visible : Visibility.Collapsed;
            ControllerDialogScrollViewer.ScrollToTop();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            if (!IsVisible) Show();
            Activate();
            ControllerDialogOverlay.Visibility = Visibility.Visible;
            Keyboard.Focus(ControllerDialogPrimaryButton);
            return await completion.Task;
        }
        finally
        {
            if (ReferenceEquals(_dialogCompletion, completion))
            {
                _dialogCompletion = null;
                ControllerDialogOverlay.Visibility = Visibility.Collapsed;
            }
            SetWorkspaceInteractive(true);
            if (dialogOrigin is UIElement { IsVisible: true, IsEnabled: true } origin)
            {
                Keyboard.Focus(origin);
            }
            else
            {
                FocusControllerDefault();
            }
            _dialogGate.Release();
        }
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

    public bool HandleControllerInput(ControllerSnapshot snapshot)
    {
        var commands = _uiInput.Process(snapshot);
        if (!IsActive) return false;

        if (ControllerDialogOverlay.Visibility == Visibility.Visible)
        {
            foreach (var command in commands) HandleControllerDialogCommand(command);
            return true;
        }
        if (BindingPickerOverlay.Visibility == Visibility.Visible)
        {
            foreach (var command in commands) HandleBindingPickerCommand(command);
            return true;
        }
        if (NameKeyboardOverlay.Visibility == Visibility.Visible)
        {
            foreach (var command in commands) HandleNameKeyboardCommand(command);
            return true;
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
        return true;
    }

    private void HandleControllerDialogCommand(ControllerUiCommand command)
    {
        switch (command)
        {
            case ControllerUiCommand.MoveUp:
                ControllerDialogScrollViewer.LineUp();
                break;
            case ControllerUiCommand.MoveDown:
                ControllerDialogScrollViewer.LineDown();
                break;
            case ControllerUiCommand.MoveLeft:
                Keyboard.Focus(ControllerDialogSecondaryButton.Visibility == Visibility.Visible
                    ? ControllerDialogSecondaryButton
                    : ControllerDialogPrimaryButton);
                break;
            case ControllerUiCommand.MoveRight:
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

    private void NavigationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WorkspaceTabs is not null && NavigationList.SelectedIndex >= 0)
        {
            WorkspaceTabs.SelectedIndex = NavigationList.SelectedIndex;
        }
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
        DPadBindings.Clear();
        FaceBindings.Clear();
        RightBindings.Clear();
        if (SelectedProfile is null) return;

        AddDisplay(LeftBindings, ControllerButton.LeftTrigger, "Left trigger", "LT");
        AddDisplay(LeftBindings, ControllerButton.LeftBumper, "Left bumper", "LB");
        AddDisplay(LeftBindings, ControllerButton.LeftStick, "Left stick click", "L3");
        AddDisplay(LeftBindings, ControllerButton.View, "View button", "▣");
        AddDisplay(LeftBindings, ControllerButton.M1, "Rear button M1", "M1");

        AddDisplay(DPadBindings, ControllerButton.DPadUp, "D-pad up", "↑");
        AddDisplay(DPadBindings, ControllerButton.DPadLeft, "D-pad left", "←");
        AddDisplay(DPadBindings, ControllerButton.DPadRight, "D-pad right", "→");
        AddDisplay(DPadBindings, ControllerButton.DPadDown, "D-pad down", "↓");

        AddDisplay(RightBindings, ControllerButton.RightTrigger, "Right trigger", "RT");
        AddDisplay(RightBindings, ControllerButton.RightBumper, "Right bumper", "RB");
        AddDisplay(RightBindings, ControllerButton.RightStick, "Right stick click", "R3");
        AddDisplay(RightBindings, ControllerButton.Menu, "Menu button", "☰");
        AddDisplay(RightBindings, ControllerButton.M2, "Rear button M2", "M2");

        AddDisplay(FaceBindings, ControllerButton.Y, "Y button", "Y");
        AddDisplay(FaceBindings, ControllerButton.X, "X button", "X");
        AddDisplay(FaceBindings, ControllerButton.B, "B button", "B");
        AddDisplay(FaceBindings, ControllerButton.A, "A button", "A");
    }

    private void AddDisplay(ICollection<ControllerBindingDisplay> destination, ControllerButton source, string label, string glyph)
    {
        var row = SelectedProfile?.Bindings.FirstOrDefault(candidate => candidate.Source == source);
        if (row is not null)
        {
            destination.Add(new ControllerBindingDisplay(label, glyph, $"Mapping-{source}", row));
        }
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
        SetWorkspaceInteractive(false);
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
        SetWorkspaceInteractive(true);
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
    private async void CaptureRearButtonSnapshot_Click(object sender, RoutedEventArgs e) => await ((App)Application.Current).CaptureRearButtonSnapshotAsync();

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
        var label = button.DataContext is ControllerBindingDisplay display ? display.Label : row.Source.ToString();
        OpenBindingPicker(row, button, label);
    }

    private void ControllerDiagramButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ControllerButton source } button || !CanEditSelected) return;
        var display = LeftBindings.Concat(DPadBindings).Concat(FaceBindings).Concat(RightBindings)
            .FirstOrDefault(candidate => candidate.Row.Source == source);
        if (display is null) return;
        OpenBindingPicker(display.Row, button, display.Label);
    }

    private void ControllerDiagram_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _diagramMouseDownPosition = e.GetPosition((IInputElement)sender);
    }

    private void ControllerDiagram_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var start = _diagramMouseDownPosition;
        _diagramMouseDownPosition = null;
        if (start.HasValue && OpenNearestDiagramControl(start.Value, e.GetPosition((IInputElement)sender))) e.Handled = true;
    }

    private void ControllerDiagram_PreviewTouchDown(object sender, TouchEventArgs e)
    {
        _diagramTouchDownPositions[e.TouchDevice.Id] = e.GetTouchPoint((IInputElement)sender).Position;
    }

    private void ControllerDiagram_PreviewTouchUp(object sender, TouchEventArgs e)
    {
        if (!_diagramTouchDownPositions.Remove(e.TouchDevice.Id, out var start)) return;
        if (OpenNearestDiagramControl(start, e.GetTouchPoint((IInputElement)sender).Position)) e.Handled = true;
    }

    private bool OpenNearestDiagramControl(Point start, Point end)
    {
        if (!CanEditSelected) return false;
        if ((end - start).LengthSquared > 12 * 12) return false;

        var startNearest = DiagramControlCenters
            .Select(control => (control.Source, DistanceSquared: (control.Center - start).LengthSquared))
            .OrderBy(control => control.DistanceSquared)
            .First();
        var endNearest = DiagramControlCenters
            .Select(control => (control.Source, DistanceSquared: (control.Center - end).LengthSquared))
            .OrderBy(control => control.DistanceSquared)
            .First();
        if (startNearest.Source != endNearest.Source ||
            startNearest.DistanceSquared > 55 * 55 || endNearest.DistanceSquared > 55 * 55) return false;

        var origin = FindDescendantButton(ControllerMapSurface, endNearest.Source);
        if (origin is null) return false;
        ControllerDiagramButton_Click(origin, new RoutedEventArgs(ButtonBase.ClickEvent));
        return true;
    }

    private static Button? FindDescendantButton(DependencyObject parent, ControllerButton source)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is Button { Tag: ControllerButton candidate } button && candidate == source) return button;
            if (FindDescendantButton(child, source) is { } descendant) return descendant;
        }
        return null;
    }

    private void OpenBindingPicker(BindingRow row, Button origin, string label)
    {
        _bindingPickerRow = row;
        _bindingPickerOrigin = origin;
        BindingPickerTitle.Text = $"Map {label}";
        BindingPickerList.ItemsSource = row.TargetOptions;
        BindingPickerList.SelectedItem = row.Target;
        SetWorkspaceInteractive(false);
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
        SetWorkspaceInteractive(true);
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

    private void SetWorkspaceInteractive(bool isInteractive)
    {
        _workspaceInteractive = isInteractive;
        NavigationList.IsEnabled = isInteractive;
        WorkspaceTabs.IsEnabled = isInteractive;
        HeaderUpdateButton.IsEnabled = isInteractive && !_updateBusy;
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
