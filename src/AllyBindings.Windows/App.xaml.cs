using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using AllyBindings.Core;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace AllyBindings.Windows;

public partial class App : System.Windows.Application
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private Mutex? _singleInstance;
    private JsonProfileStore _profileStore = null!;
    private IControllerBackend _backend = null!;
    private ProfileCycleStateMachine _cycle = null!;
    private XInputMonitor _controllerMonitor = null!;
    private GlobalPanicHotKey? _panicHotKey;
    private OverlayWindow _overlay = null!;
    private MainWindow _mainWindow = null!;
    private Forms.NotifyIcon _trayIcon = null!;
    private GitHubUpdateService _updateService = null!;
    private IReadOnlyList<string> _configurationWarnings = [];
    private bool _backendDisposed;
    private bool _backendNeedsRestore;
    private bool _mutexReleased;
    private bool _exiting;
    private bool _updateCheckInProgress;
    private bool _allowExitWithPendingRearMapping;

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

        var updateSuccessMarker = Environment.GetEnvironmentVariable("ALLY_BINDINGS_UPDATE_SUCCESS_MARKER");
        Environment.SetEnvironmentVariable("ALLY_BINDINGS_UPDATE_SUCCESS_MARKER", null);
        _ = InitializeAsync(
            e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase),
            e.Args.Contains("--updated", StringComparer.OrdinalIgnoreCase),
            updateSuccessMarker);
    }

    private async Task InitializeAsync(bool startInBackground, bool startedAfterUpdate, string? updateSuccessMarker)
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

            var recoveryWasPending = Configuration.AsusRearButtonMappingActive;
            var backendStatus = await ReplaceBackendAsync(
                Configuration.EnableAsusRearButtonMappings || recoveryWasPending,
                restoreCurrent: false);
            _backendNeedsRestore = Configuration.AsusRearButtonMappingActive;
            if (Configuration.AsusRearButtonMappingActive)
            {
                var recovery = await _backend.RestoreDefaultAsync();
                if (recovery.CommandAccepted)
                {
                    Configuration = Configuration with
                    {
                        ActiveProfileId = MappingProfile.Default.Id,
                        AsusRearButtonMappingActive = false,
                    };
                    _backendNeedsRestore = false;
                    await _profileStore.SaveAsync(Configuration);
                    _configurationWarnings = _configurationWarnings
                        .Append("Native M1/M2 reset command accepted after a previous non-default session; live state remains unreadable.")
                        .ToList();
                    backendStatus = Configuration.EnableAsusRearButtonMappings
                        ? recovery.Status
                        : await ReplaceBackendAsync(enableRearButtons: false, restoreCurrent: false);
                }
                else
                {
                    _configurationWarnings = _configurationWarnings
                        .Append($"Could not send the M1/M2 reset command: {recovery.Message}")
                        .ToList();
                }
            }
            _cycle = new ProfileCycleStateMachine(Configuration.Shortcut);
            _overlay = new OverlayWindow();
            _mainWindow = new MainWindow(Configuration, backendStatus);
            _updateService = new GitHubUpdateService();
            MainWindow = _mainWindow;
            _mainWindow.SetUpdateStatus($"Current version: {GitHubUpdateService.CurrentVersion.ToString(3)}");

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
            if (startedAfterUpdate)
            {
                _mainWindow.SetStatus($"Updated successfully to {GitHubUpdateService.CurrentVersion.ToString(3)}.");
                WriteUpdateSuccessMarker(updateSuccessMarker);
            }
            if (Configuration.CheckForUpdatesAutomatically)
            {
                _ = CheckForUpdatesAsync(userInitiated: false);
            }
        }
        catch (Exception ex)
        {
            Forms.MessageBox.Show($"Ally Bindings could not start safely. No controller settings were changed.\n\n{ex.Message}", "Ally Bindings", Forms.MessageBoxButtons.OK, Forms.MessageBoxIcon.Error);
            Shutdown();
        }
    }

    private static void WriteUpdateSuccessMarker(string? markerPath)
    {
        if (string.IsNullOrWhiteSpace(markerPath))
        {
            throw new InvalidOperationException("The updater did not provide its startup-health marker.");
        }

        var updatesRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AllyBindings",
            "updates")) + Path.DirectorySeparatorChar;
        var fullMarkerPath = Path.GetFullPath(markerPath);
        if (!fullMarkerPath.StartsWith(updatesRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The updater startup-health marker is outside the managed update directory.");
        }

        File.WriteAllText(fullMarkerPath, GitHubUpdateService.CurrentVersion.ToString(3));
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
                case CycleEventKind.ApplicationRequested:
                    _overlay.Dismiss();
                    OpenMainWindow();
                    break;
                case CycleEventKind.Cancelled:
                    _overlay.ShowResult("Selection cancelled", cycleEvent.Message ?? "Controller disconnected");
                    break;
            }
        }
    }

    private IReadOnlyList<CycleItem> BuildCycleItems()
    {
        return Configuration.Profiles
            .Where(profile => profile.Enabled)
            .Select(CycleItem.ForProfile)
            .ToList();
    }

    private async Task CommitCycleItemAsync(CycleItem item)
    {
        await ApplyProfileAsync(item.Id, showOverlay: true);
    }

    public async Task<bool> SaveEditorAsync(MainWindow editor)
    {
        await _operationGate.WaitAsync();
        try
        {
            var selectedProfileId = editor.SelectedProfile is null
                ? null
                : editor.SelectedProfile.IsDefault
                    ? MappingProfile.Default.Id
                    : ConfigurationValidator.Slugify(editor.SelectedProfile.Name);
            var candidate = editor.BuildConfiguration(Configuration.ActiveProfileId, Configuration.ControllerIndex) with
            {
                LastUpdateCheckUtc = Configuration.LastUpdateCheckUtc,
                AsusRearButtonMappingActive = Configuration.AsusRearButtonMappingActive,
            };
            var validated = ConfigurationValidator.Normalize(candidate);
            var rearBackendChanged =
                validated.Configuration.EnableAsusRearButtonMappings != Configuration.EnableAsusRearButtonMappings;
            BackendStatus? replacementStatus = null;
            if (rearBackendChanged)
            {
                replacementStatus = await ReplaceBackendAsync(
                    validated.Configuration.EnableAsusRearButtonMappings,
                    restoreCurrent: Configuration.EnableAsusRearButtonMappings);
            }
            var nextConfiguration = validated.Configuration;
            if (rearBackendChanged && !nextConfiguration.EnableAsusRearButtonMappings)
            {
                // ReplaceBackendAsync only returns after the native reset command
                // was accepted whenever recovery was required.
                nextConfiguration = nextConfiguration with { AsusRearButtonMappingActive = false };
            }
            Configuration = nextConfiguration;
            _configurationWarnings = validated.Warnings;
            await _profileStore.SaveAsync(Configuration);
            StartupRegistration.SetEnabled(Configuration.RunAtStartup);
            _cycle.UpdateShortcut(Configuration.Shortcut);
            _controllerMonitor.SetPreferredIndex(Configuration.ControllerIndex);
            editor.Load(Configuration, selectedProfileId);
            editor.SetBackendStatus(replacementStatus ?? _backend.GetStatus());
            editor.SetStatus(validated.Warnings.Count == 0 ? "Saved locally." : string.Join(" ", validated.Warnings));
            return true;
        }
        catch (Exception ex)
        {
            editor.SetStatus($"Save failed safely: {ex.Message}");
            return false;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task ApplyProfileAsync(string profileId, bool showOverlay)
    {
        await _operationGate.WaitAsync();
        try
        {
            var profile = Configuration.Profiles.FirstOrDefault(candidate => candidate.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
            {
                if (showOverlay) _overlay.ShowResult("Profile unavailable", "The selected profile no longer exists.");
                return;
            }

            var rearMappingRequested =
                Configuration.EnableAsusRearButtonMappings &&
                profile.Id != MappingProfile.Default.Id &&
                HasRearRemap(profile);
            var backendStatus = _backend.GetStatus();
            if (rearMappingRequested && !backendStatus.CanRemap)
            {
                // Reprobe before persisting recovery intent. Custom applies are
                // forbidden from reprobing internally, so no HID write can get
                // ahead of the on-disk crash marker.
                backendStatus = await _backend.InitializeAsync();
                _mainWindow.SetBackendStatus(backendStatus);
            }
            var rearWriteWillBeAttempted = rearMappingRequested && backendStatus.CanRemap;
            if (rearWriteWillBeAttempted && !_backendNeedsRestore)
            {
                // Persist the recovery intent before touching firmware. A hard
                // kill between SetFeature and the later profile save must still
                // cause a native M1/M2 reset attempt on next launch.
                Configuration = Configuration with { AsusRearButtonMappingActive = true };
                await _profileStore.SaveAsync(Configuration);
                _backendNeedsRestore = true;
            }

            var result = profile.Id == MappingProfile.Default.Id
                ? await _backend.RestoreDefaultAsync()
                : await _backend.ApplyAsync(profile);
            if (Configuration.EnableAsusRearButtonMappings && _backendNeedsRestore && !result.CommandAccepted)
            {
                _mainWindow.SetBackendStatus(result.Status);
                _mainWindow.SetStatus($"Profile not changed: {result.Message}");
                if (showOverlay)
                {
                    _overlay.ShowResult("Apply failed", "Previous M1/M2 mapping may still be active");
                }
                return;
            }
            var rearMappingActive =
                result.Status.Health == BackendHealth.Partial &&
                result.CommandAccepted &&
                profile.Id != MappingProfile.Default.Id &&
                HasRearRemap(profile);
            _backendNeedsRestore = rearMappingActive;
            Configuration = Configuration with
            {
                ActiveProfileId = profile.Id,
                AsusRearButtonMappingActive = rearMappingActive,
            };
            await _profileStore.SaveAsync(Configuration);
            _mainWindow.SetBackendStatus(result.Status);
            _mainWindow.SetStatus(result.Message);
            var trayLabel = result.Status.Health switch
            {
                BackendHealth.Partial => $"Ally Bindings · M1/M2 · {profile.Name}",
                BackendHealth.Preview => $"Ally Bindings · Preview · {profile.Name}",
                _ => $"Ally Bindings · {profile.Name}",
            };
            _trayIcon.Text = trayLabel[..Math.Min(63, trayLabel.Length)];
            if (showOverlay)
            {
                _overlay.ShowResult(
                    profile.Name,
                    result.Status.Health == BackendHealth.Partial && result.CommandAccepted
                        ? "M1/M2 write accepted · live state unverified"
                        : result.CommandAccepted
                            ? "Command accepted · live state unverified"
                            : "Selected · preview backend only");
            }
        }
        catch (Exception ex)
        {
            var safetyDetail = _backendNeedsRestore
                ? "M1/M2 state may have changed · recovery marker retained"
                : "Default passthrough kept intact";
            if (showOverlay) _overlay.ShowResult("Apply failed", $"{safetyDetail} · {ex.Message}");
            _mainWindow.SetStatus($"Apply failed safely: {safetyDetail}. {ex.Message}");
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task RestoreDefaultAsync(string reason)
    {
        await _operationGate.WaitAsync();
        try
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
                _mainWindow.SetStatus($"Native reset command failed: {ex.Message}");
                _overlay.ShowResult("Restore failed", "Use the physical controller/keyboard recovery path and open diagnostics.");
                return;
            }

            if (_backendNeedsRestore && !result.CommandAccepted)
            {
                _mainWindow.SetBackendStatus(result.Status);
                _mainWindow.SetStatus($"{reason}: native reset command was not accepted; the previous M1/M2 mapping may still be active. {result.Message}");
                _overlay.ShowResult("Restore failed", "Previous M1/M2 mapping may still be active");
                return;
            }

            Configuration = Configuration with { ActiveProfileId = MappingProfile.Default.Id };
            if (result.CommandAccepted)
            {
                _backendNeedsRestore = false;
                Configuration = Configuration with { AsusRearButtonMappingActive = false };
            }
            string? persistenceWarning = null;
            try
            {
                await _profileStore.SaveAsync(Configuration);
            }
            catch (Exception ex)
            {
                persistenceWarning = $" The reset command was accepted, but saving the Default selection failed: {ex.Message}";
            }

            _mainWindow.SetBackendStatus(result.Status);
            _mainWindow.SetStatus($"{reason}: {result.Message}{persistenceWarning}");
            _overlay.ShowResult(
                "Native reset",
                persistenceWarning is not null
                    ? "Command accepted · live state unverified · local selection not saved"
                    : result.CommandAccepted ? "Command accepted · live state unverified" : "Default selected · passthrough remains intact");
        }
        finally
        {
            _operationGate.Release();
        }
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

    public async Task CheckForUpdatesAsync(bool userInitiated)
    {
        if (_updateCheckInProgress || _updateService is null) return;
        if (!userInitiated && Configuration.LastUpdateCheckUtc is { } lastCheck &&
            DateTimeOffset.UtcNow - lastCheck < TimeSpan.FromHours(24))
        {
            return;
        }

        _updateCheckInProgress = true;
        try
        {
            _mainWindow.SetUpdateStatus("Checking GitHub Releases…");
            var includePrerelease = userInitiated
                ? _mainWindow.IncludePrereleaseUpdates
                : Configuration.IncludePrereleaseUpdates;
            var candidate = await _updateService.CheckAsync(includePrerelease);
            await _operationGate.WaitAsync();
            try
            {
                Configuration = Configuration with { LastUpdateCheckUtc = DateTimeOffset.UtcNow };
                await _profileStore.SaveAsync(Configuration);
            }
            finally
            {
                _operationGate.Release();
            }

            if (candidate is null)
            {
                _mainWindow.SetUpdateStatus($"Current version {GitHubUpdateService.CurrentVersion.ToString(3)} is up to date.");
                return;
            }

            _mainWindow.SetUpdateStatus($"Update available: {candidate.TagName}");
            var choice = Forms.MessageBox.Show(
                $"{candidate.ReleaseName} is available.\n\n" +
                "The ZIP will be downloaded from this repository, verified against GitHub's SHA-256 digest, staged safely, then installed after Ally Bindings exits.\n\n" +
                "Install and restart now?",
                "Ally Bindings update",
                Forms.MessageBoxButtons.YesNo,
                Forms.MessageBoxIcon.Information);
            if (choice != Forms.DialogResult.Yes) return;

            var progress = new Progress<double>(value =>
                _mainWindow.SetUpdateStatus($"Downloading {candidate.TagName}: {value:P0}"));
            var prepared = await _updateService.DownloadAndPrepareAsync(candidate, progress);
            if (!await ConfirmSafeExitForUpdateAsync())
            {
                try { Directory.Delete(prepared.UpdateRoot, recursive: true); } catch { }
                _mainWindow.SetUpdateStatus("Update cancelled; controller recovery was not confirmed.");
                return;
            }

            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("The current executable path is unavailable.");
            GitHubUpdateService.LaunchInstaller(prepared, Path.GetDirectoryName(executable)!, Environment.ProcessId);
            _mainWindow.SetUpdateStatus("Update verified. Restarting…");
            await ExitAsync();
        }
        catch (Exception ex)
        {
            _allowExitWithPendingRearMapping = false;
            _mainWindow.SetUpdateStatus($"Update failed safely: {ex.Message}");
            if (userInitiated)
            {
                Forms.MessageBox.Show(
                    $"The update was not installed. Existing app files were not changed.\n\n{ex.Message}",
                    "Ally Bindings update",
                    Forms.MessageBoxButtons.OK,
                    Forms.MessageBoxIcon.Error);
            }
        }
        finally
        {
            _updateCheckInProgress = false;
        }
    }

    private async Task<bool> ConfirmSafeExitForUpdateAsync()
    {
        await _operationGate.WaitAsync();
        try
        {
            if (!_backendNeedsRestore) return true;
            try
            {
                var restored = await _backend.RestoreDefaultAsync();
                if (restored.CommandAccepted)
                {
                    _backendNeedsRestore = false;
                    Configuration = Configuration with
                    {
                        ActiveProfileId = MappingProfile.Default.Id,
                        AsusRearButtonMappingActive = false,
                    };
                    await _profileStore.SaveAsync(Configuration);
                    return true;
                }
            }
            catch
            {
                // The explicit warning below is the recovery gate.
            }

            var continueWithoutReset = Forms.MessageBox.Show(
                "The best-known native M1/M2 reset could not be written. Updating now may leave the last paddle mapping active until Armoury Crate or another recovery path overwrites it.\n\nUpdate anyway?",
                "Controller recovery not confirmed",
                Forms.MessageBoxButtons.YesNo,
                Forms.MessageBoxIcon.Warning) == Forms.DialogResult.Yes;
            _allowExitWithPendingRearMapping = continueWithoutReset;
            return continueWithoutReset;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<BackendStatus> ReplaceBackendAsync(bool enableRearButtons, bool restoreCurrent)
    {
        IControllerBackend replacement = enableRearButtons
            ? new AsusRearButtonControllerBackend(new AsusRearButtonHidDevice())
            : new PreviewControllerBackend();
        var replacementStatus = await replacement.InitializeAsync();

        if (_backend is not null)
        {
            if (restoreCurrent && _backendNeedsRestore)
            {
                var restored = await _backend.RestoreDefaultAsync();
                if (!restored.CommandAccepted)
                {
                    await replacement.DisposeAsync();
                    throw new InvalidOperationException(
                        "Could not write the best-known native M1/M2 reset, so the hardware backend was not disabled.");
                }
            }
            await _backend.DisposeAsync();
        }

        _backend = replacement;
        _backendDisposed = false;
        _backendNeedsRestore = false;
        return replacementStatus;
    }

    private static bool HasRearRemap(MappingProfile profile) =>
        profile.Bindings.GetValueOrDefault(ControllerButton.M1, ControllerButton.M1) != ControllerButton.M1 ||
        profile.Bindings.GetValueOrDefault(ControllerButton.M2, ControllerButton.M2) != ControllerButton.M2;

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
        await _operationGate.WaitAsync();
        var shouldShutdown = true;

        try
        {
            string? restoreFailure = null;
            if (_backendNeedsRestore)
            {
                try
                {
                    var restored = await _backend.RestoreDefaultAsync();
                    if (restored.CommandAccepted)
                    {
                        _backendNeedsRestore = false;
                        Configuration = Configuration with
                        {
                            ActiveProfileId = MappingProfile.Default.Id,
                            AsusRearButtonMappingActive = false,
                        };
                        await _profileStore.SaveAsync(Configuration);
                    }
                    else
                    {
                        restoreFailure = restored.Message;
                    }
                }
                catch (Exception ex)
                {
                    restoreFailure = ex.Message;
                }
            }

            if (_backendNeedsRestore && !_allowExitWithPendingRearMapping)
            {
                var exitAnyway = Forms.MessageBox.Show(
                    "The best-known native M1/M2 reset failed. Exiting may leave the last paddle mapping active until Armoury Crate or another recovery path overwrites it.\n\n" +
                    $"Details: {restoreFailure ?? "No interface accepted the reset."}\n\nExit anyway?",
                    "Controller recovery not confirmed",
                    Forms.MessageBoxButtons.YesNo,
                    Forms.MessageBoxIcon.Warning) == Forms.DialogResult.Yes;
                if (!exitAnyway)
                {
                    _exiting = false;
                    shouldShutdown = false;
                    _mainWindow.SetStatus("Exit cancelled; M1/M2 recovery is still required.");
                    return;
                }
            }

            TryCleanup(() => _controllerMonitor.Stop());
            try
            {
                await _backend.DisposeAsync();
                _backendDisposed = true;
            }
            catch
            {
                // Continue process cleanup; the persisted recovery marker remains
                // set if the native reset was not confirmed.
            }

            TryCleanup(() => _panicHotKey?.Dispose());
            TryCleanup(() => _updateService?.Dispose());
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
            _operationGate.Release();
            if (shouldShutdown) Shutdown();
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
        TryCleanup(() => _updateService?.Dispose());
        TryCleanup(() => _controllerMonitor?.Dispose());
        TryCleanup(() => _panicHotKey?.Dispose());
        ReleaseSingleInstanceMutex();
        base.OnExit(e);
    }

    private void RestoreAndDisposeForTermination()
    {
        if (_backend is null || _backendDisposed) return;
        var backend = _backend;
        try
        {
            if (_backendNeedsRestore)
            {
                // Session-ending callbacks are synchronous WPF hooks. Start the
                // async restoration on the thread pool so blocking this dispatcher
                // cannot deadlock its continuations.
                var restoreTask = Task.Run(async () =>
                    await backend.RestoreDefaultAsync().ConfigureAwait(false));
                if (restoreTask.Wait(TimeSpan.FromSeconds(7)) && restoreTask.Result.CommandAccepted)
                {
                    _backendNeedsRestore = false;
                }
            }
        }
        catch
        {
            // Session termination cannot be held hostage by a broken backend.
        }
        try
        {
            var disposeTask = Task.Run(async () =>
                await backend.DisposeAsync().ConfigureAwait(false));
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
