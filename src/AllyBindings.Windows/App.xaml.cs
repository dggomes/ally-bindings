using System.Reflection;
using System.Threading;
using System.Windows;
using AllyBindings.Core;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace AllyBindings.Windows;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private JsonProfileStore _profileStore = null!;
    private IControllerBackend _backend = null!;
    private ProfileCycleStateMachine _cycle = null!;
    private XInputMonitor _controllerMonitor = null!;
    private GlobalPanicHotKey? _panicHotKey;
    private OverlayWindow _overlay = null!;
    private MainWindow _mainWindow = null!;
    private Forms.NotifyIcon _trayIcon = null!;
    private IReadOnlyList<string> _configurationWarnings = [];
    private bool _backendDisposed;
    private bool _mutexReleased;
    private bool _exiting;

    public AppConfiguration Configuration { get; private set; } = AppConfiguration.CreateDefault();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\AllyBindings.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Forms.MessageBox.Show("Ally Bindings is already running in the notification area.", "Ally Bindings", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Information);
            Shutdown();
            return;
        }

        _ = InitializeAsync(e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase));
    }

    private async Task InitializeAsync(bool startInBackground)
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var configPath = System.IO.Path.Combine(appData, "AllyBindings", "config.json");
            _profileStore = new JsonProfileStore(configPath);
            var loaded = await _profileStore.LoadAsync();
            Configuration = loaded.Configuration;
            _configurationWarnings = loaded.Warnings;

            var startupCommand = StartupRegistration.CurrentCommand;
            var startupEnabled = StartupRegistration.IsEnabled();
            if (startupCommand is not null && !startupEnabled)
            {
                StartupRegistration.SetEnabled(false);
                _configurationWarnings = _configurationWarnings.Append("Removed a stale run-at-login entry that pointed at a different executable.").ToList();
            }
            if (Configuration.RunAtStartup != startupEnabled)
            {
                Configuration = Configuration with { RunAtStartup = startupEnabled };
                await _profileStore.SaveAsync(Configuration);
                _configurationWarnings = _configurationWarnings.Append("Run-at-login preference was reconciled with the current Windows registration.").ToList();
            }

            _backend = new PreviewControllerBackend();
            var backendStatus = await _backend.InitializeAsync();
            _cycle = new ProfileCycleStateMachine(Configuration.Shortcut);
            _overlay = new OverlayWindow();
            _mainWindow = new MainWindow(Configuration, backendStatus);
            MainWindow = _mainWindow;

            ConfigureTray();
            _controllerMonitor = new XInputMonitor(Configuration.ControllerIndex);
            _controllerMonitor.SnapshotReceived += ControllerSnapshotReceived;
            _controllerMonitor.ActiveControllerChanged += (_, index) => _mainWindow.SetControllerStatus(index);
            _controllerMonitor.Start();

            _panicHotKey = new GlobalPanicHotKey();
            _panicHotKey.Pressed += async (_, _) => await RestoreDefaultAsync("Panic shortcut");
            if (!_panicHotKey.IsRegistered)
            {
                _configurationWarnings = _configurationWarnings.Append("Ctrl+Alt+F12 could not be registered; another application may own it.").ToList();
            }

            if (!startInBackground)
            {
                OpenMainWindow();
            }
            else
            {
                _trayIcon.ShowBalloonTip(1800, "Ally Bindings", "Running in the tray. Hold your configured controller chord to rotate profiles.", Forms.ToolTipIcon.Info);
            }

            if (_configurationWarnings.Count > 0)
            {
                _mainWindow.SetStatus(string.Join(" ", _configurationWarnings));
            }
        }
        catch (Exception ex)
        {
            Forms.MessageBox.Show($"Ally Bindings could not start safely. No controller settings were changed.\n\n{ex.Message}", "Ally Bindings", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
            Shutdown();
        }
    }

    private void ConfigureTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Ally Bindings", null, (_, _) => Dispatcher.Invoke(OpenMainWindow));
        menu.Items.Add("Restore Default", null, async (_, _) => await DispatchAsync(() => RestoreDefaultAsync("Tray panic action")));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, async (_, _) => await DispatchAsync(ExitAsync));

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "Ally Bindings · Preview backend",
            Icon = Drawing.SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = menu,
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(OpenMainWindow);
    }

    private Task DispatchAsync(Func<Task> action)
    {
        return Dispatcher.CheckAccess()
            ? action()
            : Dispatcher.InvokeAsync(action).Task.Unwrap();
    }

    private void ControllerSnapshotReceived(object? sender, ControllerSnapshot snapshot)
    {
        var events = _cycle.Process(snapshot, DateTimeOffset.UtcNow, BuildCycleItems(), Configuration.ActiveProfileId);
        foreach (var cycleEvent in events)
        {
            switch (cycleEvent.Kind)
            {
                case CycleEventKind.SelectionChanged when cycleEvent.Item is not null:
                    _overlay.ShowSelection(cycleEvent.Item.Label);
                    break;
                case CycleEventKind.SelectionCommitted when cycleEvent.Item is not null:
                    _ = CommitCycleItemAsync(cycleEvent.Item);
                    break;
                case CycleEventKind.Cancelled:
                    _overlay.ShowResult("Selection cancelled", cycleEvent.Message ?? "Controller disconnected");
                    break;
            }
        }
    }

    private IReadOnlyList<CycleItem> BuildCycleItems()
    {
        var items = Configuration.Profiles
            .Where(profile => profile.Enabled)
            .Select(CycleItem.ForProfile)
            .ToList();
        items.Add(CycleItem.OpenApplication);
        return items;
    }

    private async Task CommitCycleItemAsync(CycleItem item)
    {
        if (item.Kind == CycleItemKind.OpenApplication)
        {
            _overlay.Dismiss();
            OpenMainWindow();
            return;
        }
        await ApplyProfileAsync(item.Id, showOverlay: true);
    }

    public async Task<bool> SaveEditorAsync(MainWindow editor)
    {
        try
        {
            var selectedProfileId = editor.SelectedProfile is null
                ? null
                : editor.SelectedProfile.IsDefault
                    ? MappingProfile.Default.Id
                    : ConfigurationValidator.Slugify(editor.SelectedProfile.Name);
            var candidate = editor.BuildConfiguration(Configuration.ActiveProfileId, Configuration.ControllerIndex);
            var validated = ConfigurationValidator.Normalize(candidate);
            Configuration = validated.Configuration;
            _configurationWarnings = validated.Warnings;
            await _profileStore.SaveAsync(Configuration);
            StartupRegistration.SetEnabled(Configuration.RunAtStartup);
            _cycle.UpdateShortcut(Configuration.Shortcut);
            _controllerMonitor.SetPreferredIndex(Configuration.ControllerIndex);
            editor.Load(Configuration, selectedProfileId);
            editor.SetBackendStatus(_backend.GetStatus());
            editor.SetStatus(validated.Warnings.Count == 0 ? "Saved locally." : string.Join(" ", validated.Warnings));
            return true;
        }
        catch (Exception ex)
        {
            editor.SetStatus($"Save failed safely: {ex.Message}");
            return false;
        }
    }

    public async Task ApplyProfileAsync(string profileId, bool showOverlay)
    {
        var profile = Configuration.Profiles.FirstOrDefault(candidate => candidate.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            if (showOverlay) _overlay.ShowResult("Profile unavailable", "The selected profile no longer exists.");
            return;
        }

        try
        {
            var result = profile.Id == MappingProfile.Default.Id
                ? await _backend.RestoreDefaultAsync()
                : await _backend.ApplyAsync(profile);
            Configuration = Configuration with { ActiveProfileId = profile.Id };
            await _profileStore.SaveAsync(Configuration);
            _mainWindow.SetBackendStatus(result.Status);
            _mainWindow.SetStatus(result.Message);
            var trayLabel = result.Status.Health == BackendHealth.Preview
                ? $"Ally Bindings · Preview · {profile.Name}"
                : $"Ally Bindings · {profile.Name}";
            _trayIcon.Text = trayLabel[..Math.Min(63, trayLabel.Length)];
            if (showOverlay)
            {
                _overlay.ShowResult(profile.Name, result.AppliedToController ? "Applied to controller" : "Selected · preview backend only");
            }
        }
        catch (Exception ex)
        {
            if (showOverlay) _overlay.ShowResult("Apply failed", $"Default passthrough kept intact · {ex.Message}");
            _mainWindow.SetStatus($"Apply failed safely: {ex.Message}");
        }
    }

    public async Task RestoreDefaultAsync(string reason)
    {
        _cycle.Cancel();
        BackendApplyResult result;
        try
        {
            result = await _backend.RestoreDefaultAsync();
        }
        catch (Exception ex)
        {
            _mainWindow.SetBackendStatus(_backend.GetStatus());
            _mainWindow.SetStatus($"Default restore failed: {ex.Message}");
            _overlay.ShowResult("Restore failed", "Use the physical controller/keyboard recovery path and open diagnostics.");
            return;
        }

        Configuration = Configuration with { ActiveProfileId = MappingProfile.Default.Id };
        string? persistenceWarning = null;
        try
        {
            await _profileStore.SaveAsync(Configuration);
        }
        catch (Exception ex)
        {
            persistenceWarning = $" Controller state was restored, but saving the Default selection failed: {ex.Message}";
        }

        _mainWindow.SetBackendStatus(result.Status);
        _mainWindow.SetStatus($"{reason}: {result.Message}{persistenceWarning}");
        _overlay.ShowResult(
            "Default",
            persistenceWarning is not null
                ? "Controller restored · local selection could not be saved"
                : result.AppliedToController ? "Controller mapping restored" : "Default selected · passthrough remains intact");
    }

    public string BuildDiagnostics()
    {
        var snapshot = new DiagnosticsSnapshot(
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "development",
            Environment.OSVersion.VersionString,
            System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
            DateTimeOffset.UtcNow,
            _backend.GetStatus(),
            Configuration.Profiles.Count,
            Configuration.ActiveProfileId,
            _controllerMonitor.ActiveControllerIndex,
            _configurationWarnings);
        return DiagnosticsExporter.ToJson(snapshot);
    }

    public void OpenMainWindow()
    {
        if (_mainWindow.WindowState == WindowState.Minimized) _mainWindow.WindowState = WindowState.Normal;
        if (!_mainWindow.IsVisible) _mainWindow.Show();
        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;

        try
        {
            TryCleanup(() => _controllerMonitor.Stop());
            try
            {
                await _backend.RestoreDefaultAsync();
            }
            catch
            {
                // Continue into fail-open disposal even if explicit restoration fails.
            }

            try
            {
                await _backend.DisposeAsync();
                _backendDisposed = true;
            }
            catch
            {
                // Process/UI cleanup must continue; real backends must also fail open on process exit.
            }

            TryCleanup(() => _panicHotKey?.Dispose());
            TryCleanup(() => _controllerMonitor.Dispose());
            TryCleanup(() => _trayIcon.Visible = false);
            TryCleanup(() => _trayIcon.Dispose());
            TryCleanup(() => _overlay.Close());
            TryCleanup(() => _mainWindow.AllowClose());
            TryCleanup(() => _mainWindow.Close());
            ReleaseSingleInstanceMutex();
        }
        finally
        {
            Shutdown();
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        RestoreAndDisposeForTermination();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_backendDisposed)
        {
            RestoreAndDisposeForTermination();
        }
        TryCleanup(() => _trayIcon?.Dispose());
        TryCleanup(() => _controllerMonitor?.Dispose());
        TryCleanup(() => _panicHotKey?.Dispose());
        ReleaseSingleInstanceMutex();
        base.OnExit(e);
    }

    private void RestoreAndDisposeForTermination()
    {
        if (_backend is null || _backendDisposed) return;
        try
        {
            _backend.RestoreDefaultAsync().Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Session termination cannot be held hostage by a broken backend.
        }
        try
        {
            var disposeTask = _backend.DisposeAsync().AsTask();
            _backendDisposed = disposeTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // A real backend must additionally restore passthrough from its process-exit path.
        }
    }

    private void ReleaseSingleInstanceMutex()
    {
        if (_mutexReleased) return;
        _mutexReleased = true;
        try
        {
            _singleInstance?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // This process did not own the mutex (for example, the second-instance exit path).
        }
        TryCleanup(() => _singleInstance?.Dispose());
    }

    private static void TryCleanup(Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch
        {
            // Best-effort teardown: continue through every cleanup action.
        }
    }
}
