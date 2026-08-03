using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using AllyBindings.Core;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace AllyBindings.Windows;

public partial class App : System.Windows.Application
{
    private const string ActivationPipeName = "AllyBindings.Activation.v1";
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly ControllerInputArbitration _controllerInputArbitration = new();
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
    private volatile bool _armouryCaptureInProgress;
    private volatile bool _armouryCaptureTeardownUnconfirmed;
    private CancellationTokenSource? _armouryCaptureCancellation;
    private TaskCompletionSource? _armouryCaptureCompletion;
    private bool _allowExitWithPendingRearMapping;
    private System.IO.FileStream? _executableIntegrityLock;
    private CancellationTokenSource? _activationListenerCancellation;
    private Task? _activationListenerTask;
    private bool _activationRequested;

    public AppConfiguration Configuration { get; private set; } = AppConfiguration.CreateDefault();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (ArmouryTapCaptureHelper.TryParseArguments(e.Args, out var tapSessionId, out var tapParentProcessId))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunTapCaptureHelperAsync(tapSessionId, tapParentProcessId);
            return;
        }
        if (ArmouryEtwCaptureHelper.TryParseArguments(e.Args, out var etwSessionId, out var parentProcessId))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunEtwCaptureHelperAsync(etwSessionId, parentProcessId);
            return;
        }

        var executablePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Windows did not expose the current Ally Bindings executable path.");
        // Keep the exact on-disk image read-only for this process lifetime. The
        // same locked path is what ShellExecute elevates for ETW capture, closing
        // the user-writable-directory replacement window. The updater replaces
        // it only after this process exits and releases the handle.
        _executableIntegrityLock = new System.IO.FileStream(
            executablePath,
            System.IO.FileMode.Open,
            System.IO.FileAccess.Read,
            System.IO.FileShare.Read);

        _singleInstance = new Mutex(initiallyOwned: true, @"Local\AllyBindings.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            if (!SignalExistingInstance())
            {
                Forms.MessageBox.Show(
                    "Ally Bindings is already running, but its window could not be opened. Use the notification-area icon.",
                    "Ally Bindings",
                    Forms.MessageBoxButtons.OK,
                    Forms.MessageBoxIcon.Information);
            }
            Shutdown();
            return;
        }

        StartActivationListener();

        var updateSuccessMarker = Environment.GetEnvironmentVariable("ALLY_BINDINGS_UPDATE_SUCCESS_MARKER");
        Environment.SetEnvironmentVariable("ALLY_BINDINGS_UPDATE_SUCCESS_MARKER", null);
        _ = InitializeAsync(
            e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase),
            e.Args.Contains("--updated", StringComparer.OrdinalIgnoreCase),
            updateSuccessMarker);
    }

    private async Task RunEtwCaptureHelperAsync(Guid sessionId, int parentProcessId)
    {
        var exitCode = await ArmouryEtwCaptureHelper.RunAsync(sessionId, parentProcessId);
        Shutdown(exitCode);
    }

    private async Task RunTapCaptureHelperAsync(Guid sessionId, int parentProcessId)
    {
        var exitCode = await ArmouryTapCaptureHelper.RunAsync(sessionId, parentProcessId);
        Shutdown(exitCode);
    }

    private static bool SignalExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                ActivationPipeName,
                PipeDirection.Out,
                PipeOptions.CurrentUserOnly);
            client.Connect(2500);
            using var writer = new StreamWriter(client, new UTF8Encoding(false), 256, leaveOpen: false)
            {
                AutoFlush = true,
            };
            writer.WriteLine("open");
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void StartActivationListener()
    {
        _activationListenerCancellation = new CancellationTokenSource();
        _activationListenerTask = ListenForActivationAsync(_activationListenerCancellation.Token);
    }

    private async Task ListenForActivationAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    ActivationPipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                using var reader = new StreamReader(server, Encoding.UTF8, false, 256, leaveOpen: true);
                var command = await reader.ReadLineAsync(timeout.Token);
                if (string.Equals(command, "open", StringComparison.Ordinal))
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (_mainWindow is null)
                        {
                            _activationRequested = true;
                        }
                        else
                        {
                            OpenMainWindow();
                        }
                    });
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // A malformed or abandoned same-user peer cannot kill activation;
                // recreate the private listener for the next normal launch.
            }
        }
    }

    private void StopActivationListener()
    {
        var cancellation = Interlocked.Exchange(ref _activationListenerCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
        _activationListenerTask = null;
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

            if (Configuration.ArmouryTapTeardownBlockedSinceUtc is not null)
            {
                var currentBootIdentifier = TryGetCurrentBootIdentifier();
                if (Configuration.ArmouryTapTeardownBootIdentifier is { } blockedBootIdentifier &&
                    currentBootIdentifier is { } currentBoot && currentBoot != blockedBootIdentifier)
                {
                    Configuration = Configuration with
                    {
                        ArmouryTapTeardownBlockedSinceUtc = null,
                        ArmouryTapTeardownBootIdentifier = null,
                    };
                    await _profileStore.SaveAsync(Configuration);
                    _configurationWarnings = _configurationWarnings
                        .Append("Cleared the persisted Armoury tap write barrier after a Windows restart proved the affected processes exited.")
                        .ToList();
                }
                else
                {
                    _armouryCaptureTeardownUnconfirmed = true;
                    _armouryCaptureInProgress = true;
                    _armouryCaptureCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _armouryCaptureCompletion.SetException(new InvalidOperationException(
                        "A previous native tap unload was not confirmed. Restart Windows before controller writes can resume."));
                    _ = _armouryCaptureCompletion.Task.Exception;
                    _configurationWarnings = _configurationWarnings
                        .Append("Controller writes remain blocked because a previous native tap unload was not confirmed. Restart Windows; restarting Ally Bindings alone is not sufficient.")
                        .ToList();
                }
            }

            if (Configuration.EnableAsusRearButtonMappings && !ArmouryProtocolValidation.IsOperationApproved(isRecoveryReset: false))
            {
                Configuration = Configuration with { EnableAsusRearButtonMappings = false };
                await _profileStore.SaveAsync(Configuration);
                _configurationWarnings = _configurationWarnings
                    .Append("Experimental ASUS writes were disabled because physical Armoury protocol validation is still pending.")
                    .ToList();
            }

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

            var recoveryWasPending = Configuration.AsusRearButtonMappingActive &&
                !_armouryCaptureTeardownUnconfirmed;
            var backendStatus = await ReplaceBackendAsync(
                Configuration.EnableAsusRearButtonMappings || recoveryWasPending,
                restoreCurrent: false,
                allowUnverifiedRecoveryReset:
                    recoveryWasPending && ArmouryProtocolValidation.RecoveryWritesApproved);
            _backendNeedsRestore = Configuration.AsusRearButtonMappingActive;
            if (Configuration.AsusRearButtonMappingActive && ArmouryProtocolValidation.RecoveryWritesApproved)
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
            else if (Configuration.AsusRearButtonMappingActive)
            {
                _configurationWarnings = _configurationWarnings
                    .Append("A stale M1/M2 recovery marker exists, but this capture-only build sent no reset report. Restore through Armoury Crate.")
                    .ToList();
            }
            _cycle = new ProfileCycleStateMachine(Configuration.Shortcut);
            _overlay = new OverlayWindow();
            _mainWindow = new MainWindow(Configuration, backendStatus);
            _updateService = new GitHubUpdateService();
            MainWindow = _mainWindow;
            _mainWindow.SetUpdateStatus($"Current version: {GitHubUpdateService.CurrentSemanticVersion}");

            ConfigureTray();
            _controllerMonitor = new XInputMonitor(Configuration.ControllerIndex);
            _controllerMonitor.SnapshotReceived += ControllerSnapshotReceived;
            _controllerMonitor.ActiveControllerChanged += (_, index) => _mainWindow.SetControllerStatus(index);
            _controllerMonitor.Start();
            _mainWindow.SetControllerStatus(_controllerMonitor.ActiveControllerIndex);

            _panicHotKey = new GlobalPanicHotKey();
            _panicHotKey.Pressed += async (_, _) => await RestoreDefaultAsync("Panic shortcut");
            if (!_panicHotKey.IsRegistered)
            {
                _configurationWarnings = _configurationWarnings.Append("Ctrl+Alt+F12 could not be registered; another application may own it.").ToList();
            }

            if (!startInBackground || _activationRequested)
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
            Text = $"Ally Bindings {GitHubUpdateService.CurrentSemanticVersion} · Preview",
            Icon = LoadApplicationIcon(),
            Visible = true,
            ContextMenuStrip = menu,
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(OpenMainWindow);
    }

    private static Drawing.Icon LoadApplicationIcon()
    {
        var executable = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executable))
        {
            using var extracted = Drawing.Icon.ExtractAssociatedIcon(executable);
            if (extracted is not null)
            {
                return (Drawing.Icon)extracted.Clone();
            }
        }
        return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
    }

    private Task DispatchAsync(Func<Task> action)
    {
        return Dispatcher.CheckAccess()
            ? action()
            : Dispatcher.InvokeAsync(action).Task.Unwrap();
    }

    private void ControllerSnapshotReceived(object? sender, ControllerSnapshot snapshot)
    {
        var consumedByEditor = _mainWindow.HandleControllerInput(snapshot);
        var routing = _controllerInputArbitration.Route(snapshot, consumedByEditor);
        if (routing.CancelCycle)
        {
            _cycle.Cancel();
            _overlay.Dismiss();
        }
        if (!routing.ShouldProcess) return;

        var events = _cycle.Process(routing.Snapshot, DateTimeOffset.UtcNow, BuildCycleItems(), Configuration.ActiveProfileId);
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
            if (_armouryCaptureInProgress)
            {
                throw new InvalidOperationException("Save is blocked while Armoury capture is active. Cancel or finish the capture first.");
            }
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
            if (validated.Configuration.EnableAsusRearButtonMappings && !ArmouryProtocolValidation.IsOperationApproved(isRecoveryReset: false))
            {
                throw new InvalidOperationException(ArmouryProtocolValidation.GateMessage);
            }
            var rearBackendChanged =
                validated.Configuration.EnableAsusRearButtonMappings != Configuration.EnableAsusRearButtonMappings;
            BackendStatus? replacementStatus = null;
            if (rearBackendChanged)
            {
                replacementStatus = await ReplaceBackendAsync(
                    validated.Configuration.EnableAsusRearButtonMappings,
                    restoreCurrent: Configuration.EnableAsusRearButtonMappings,
                    allowUnverifiedRecoveryReset: false);
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
            if (_armouryCaptureInProgress)
            {
                _mainWindow.SetStatus("Profile changes are blocked while Armoury capture is active.");
                return;
            }
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
        await using var resetGate = await CaptureResetGate.AcquireWhenCaptureStoppedAsync(
            _operationGate,
            () =>
            {
                if (!_armouryCaptureInProgress) return null;
                return _armouryCaptureCompletion?.Task
                    ?? throw new InvalidOperationException("Capture teardown tracking is unavailable while capture is active.");
            },
            RequestArmouryCaptureCancellation);
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

    public async Task CaptureRearButtonSnapshotAsync()
    {
        await _operationGate.WaitAsync();
        try
        {
            if (_exiting || _armouryCaptureInProgress) return;
            _armouryCaptureInProgress = true;
            _armouryCaptureCancellation = new CancellationTokenSource();
            _armouryCaptureCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        finally
        {
            _operationGate.Release();
        }

        var cancellationToken = _armouryCaptureCancellation.Token;
        string? deferredFailureMessage = null;
        async Task RequireSnapshotStepAsync(string message, string title)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await _mainWindow.ShowControllerDialogAsync(title, message, primaryLabel: "Done"))
            {
                throw new OperationCanceledException(
                    "The read-only snapshot was cancelled and no bundle was created.",
                    cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }

        try
        {
            _mainWindow.SetArmouryCaptureBusy(true);
            var proceed = await _mainWindow.ShowControllerDialogAsync(
                "Snapshot M1/M2 state · read-only",
                "This discovery experiment issues four target-scoped HID GET_FEATURE requests for ASUS report 0x5A: one baseline and one after each Armoury action. GET_FEATURE is an active USB request, but it reads only.\n\n" +
                "It uses no administrator elevation, ETW, system-wide trace, named pipe, driver, SET_FEATURE call or M1/M2 write. Successful report bytes are private controller-configuration diagnostics and can never unlock writes automatically.\n\nContinue?",
                primaryLabel: "Continue");
            if (!proceed) return;
            cancellationToken.ThrowIfCancellationRequested();

            _cycle.Cancel();
            var snapshotService = new AsusFeatureReportSnapshotService();
            _mainWindow.SetRearButtonSnapshotStatus("Confirming the supported ASUS report 0x5A interface…");
            var target = await snapshotService.DiscoverTargetAsync(cancellationToken);
            await RequireSnapshotStepAsync(
                $"Confirm this is the ROG Ally controller you intend to inspect:\n\nModel: {target.Model}\nCompatible ASUS HID interfaces: {string.Join(" | ", target.DeviceIds)}\n\nNo report has been read yet. Click Cancel if this identity is unexpected.",
                "Confirm read-only snapshot target");

            var captures = new List<AsusFeatureReportSnapshotCapture>(4);
            _mainWindow.SetRearButtonSnapshotStatus("Reading baseline report 0x5A once…");
            captures.Add(await snapshotService.ReadStageAsync(
                target,
                AsusFeatureReportSnapshotStage.Baseline,
                cancellationToken));

            await RequireSnapshotStepAsync(
                "In Armoury Crate, set M1 to A and M2 to B. Wait until Armoury shows the assignment as applied, then return here and choose Done. Ally Bindings will then perform one read-only report 0x5A request.",
                "Snapshot step 1 of 3 · M1=A, M2=B");
            _mainWindow.SetRearButtonSnapshotStatus("Reading the M1=A / M2=B state once…");
            captures.Add(await snapshotService.ReadStageAsync(
                target,
                AsusFeatureReportSnapshotStage.M1A_M2B,
                cancellationToken));

            await RequireSnapshotStepAsync(
                "In Armoury Crate, now set M1 to X and M2 to Y. Wait until it is applied, then return here and choose Done. Ally Bindings will perform one read-only request.",
                "Snapshot step 2 of 3 · M1=X, M2=Y");
            _mainWindow.SetRearButtonSnapshotStatus("Reading the M1=X / M2=Y state once…");
            captures.Add(await snapshotService.ReadStageAsync(
                target,
                AsusFeatureReportSnapshotStage.M1X_M2Y,
                cancellationToken));

            await RequireSnapshotStepAsync(
                "In Armoury Crate, use Reset to Default for M1/M2. Wait until the defaults are applied, then return here and choose Done. Ally Bindings will perform the final read-only request.",
                "Snapshot step 3 of 3 · Armoury defaults");
            _mainWindow.SetRearButtonSnapshotStatus("Reading the reset-to-default state once…");
            captures.Add(await snapshotService.ReadStageAsync(
                target,
                AsusFeatureReportSnapshotStage.ResetToDefault,
                cancellationToken));

            var result = await snapshotService.CompleteAsync(target, captures, cancellationToken);
            _mainWindow.SetRearButtonSnapshotStatus(
                $"Snapshot complete — review required: {result.SuccessfulStageCount}/4 readable stage(s). Bundle SHA-256: {result.Hash}. Bundle: {result.BundlePath}");
            _mainWindow.SetStatus(
                $"Read-only report 0x5A snapshot captured with zero write authority: {string.Join(" ", result.Analysis.Reasons)}");
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{result.BundlePath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _mainWindow.SetRearButtonSnapshotStatus(
                ex is OperationCanceledException
                    ? $"Snapshot cancelled safely: {ex.Message}"
                    : $"Snapshot failed safely: {ex.Message}");
            if (ex is not OperationCanceledException) deferredFailureMessage = ex.Message;
        }
        finally
        {
            TaskCompletionSource? completion;
            await _operationGate.WaitAsync();
            try
            {
                _mainWindow.SetArmouryCaptureBusy(false);
                _armouryCaptureCancellation?.Dispose();
                _armouryCaptureCancellation = null;
                completion = _armouryCaptureCompletion;
                _armouryCaptureInProgress = false;
                _armouryCaptureCompletion = null;
            }
            finally
            {
                _operationGate.Release();
            }
            completion?.TrySetResult();
        }

        if (deferredFailureMessage is not null)
        {
            await _mainWindow.ShowControllerDialogAsync(
                "Read-only snapshot failed",
                deferredFailureMessage,
                allowCancel: false,
                primaryLabel: "OK");
        }
    }

    public async Task CaptureArmouryProtocolAsync()
    {
        await _operationGate.WaitAsync();
        try
        {
            if (_exiting || _armouryCaptureInProgress) return;
            _armouryCaptureInProgress = true;
            _armouryCaptureCancellation = new CancellationTokenSource();
            _armouryCaptureCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        finally
        {
            _operationGate.Release();
        }
        var cancellationToken = _armouryCaptureCancellation.Token;
        ArmouryCaptureSession? session = null;
        Exception? captureTeardownFailure = null;
        string? deferredFailureMessage = null;
        ArmouryCaptureException? deferredDiagnostic = null;
        string? deferredDiagnosticText = null;

        async Task RequireCaptureStepAsync(string message, string title)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await _mainWindow.ShowControllerDialogAsync(title, message, primaryLabel: "Done"))
            {
                throw new OperationCanceledException("The capture was cancelled. The integrated ETW session was stopped and no bundle was accepted as evidence.");
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        try
        {
            _mainWindow.SetArmouryCaptureBusy(true);
            cancellationToken.ThrowIfCancellationRequested();
            var proceed = await _mainWindow.ShowControllerDialogAsync(
                "Capture Armoury M1/M2 protocol",
                "This starts a temporary user-mode capture inside Ally Bindings. Close games and anti-cheat software before continuing. Windows will request administrator approval to inject a capture-only DLL into the confirmed ASUS Armoury process. No driver, Wireshark, USBPcap, WinDbg, Frida or separate tool is installed. The tap observes Armoury's HID writes without altering them.\n\n" +
                "You will deliberately change M1/M2 three times through Armoury Crate so we can collect exact wire payloads for hardware review. Ally Bindings will send no HID reports, cannot clear recovery state from this capture, and its ASUS write backend remains source locked.\n\nContinue?",
                primaryLabel: "Continue");
            if (!proceed) return;
            cancellationToken.ThrowIfCancellationRequested();

            _cycle.Cancel();
            _mainWindow.SetArmouryCaptureStatus("Confirming the ASUS feature-report interface…");
            var captureService = new ArmouryCaptureService();
            var target = await captureService.DiscoverTargetAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await RequireCaptureStepAsync(
                $"Confirm this is the ROG Ally controller you intend to inspect:\n\nModel: {target.Model}\nCompatible ASUS HID interfaces: {string.Join(" | ", target.DeviceIds)}\n\nNo ETW session has started yet. Click Cancel if this identity is unexpected.",
                "Confirm integrated Windows capture");
            session = await captureService.StartAsync(target, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _mainWindow.SetArmouryCaptureStatus(
                $"Capture running through {string.Join(" + ", session.EnabledProviders)}. Follow the staged baseline, A/B, X/Y, and reset prompts; this app remains write-locked.");
            await RequireCaptureStepAsync(
                "Baseline: leave the current M1/M2 assignments untouched briefly, then choose Done. Do not apply an Armoury change during this baseline window.",
                "Capture baseline · no change");

            await captureService.MarkActionAsync(session, "step-started-m1-a-m2-b", cancellationToken);
            await RequireCaptureStepAsync(
                "In Armoury Crate, set M1 to A and M2 to B. Wait until Armoury shows the assignment as applied, then return here and choose Done.",
                "Capture step 1 of 3 · M1=A, M2=B");
            await captureService.MarkActionAsync(session, "armoury-applied-m1-a-m2-b", cancellationToken);

            await captureService.MarkActionAsync(session, "step-started-m1-x-m2-y", cancellationToken);
            await RequireCaptureStepAsync(
                "In Armoury Crate, now set M1 to X and M2 to Y. Wait until it is applied, then return here and choose Done.",
                "Capture step 2 of 3 · M1=X, M2=Y");
            await captureService.MarkActionAsync(session, "armoury-applied-m1-x-m2-y", cancellationToken);

            await captureService.MarkActionAsync(session, "step-started-reset-to-default", cancellationToken);
            await RequireCaptureStepAsync(
                "In Armoury Crate, use its Reset to Default action for M1/M2. Wait until the defaults are applied, then return here and choose Done. This captures Armoury's real recovery bytes.",
                "Capture step 3 of 3 · Armoury defaults");
            await captureService.MarkActionAsync(session, "armoury-reset-m1-m2-to-default", cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            var result = await captureService.CompleteAsync(session, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            session = null;
            _mainWindow.SetArmouryCaptureStatus(
                $"Capture complete — review required: {result.FeatureReportCount} report 0x5A candidate(s), {result.RearMappingReportCount} structurally valid candidate(s). Bundle SHA-256: {result.BundleSha256}. Bundle: {result.BundlePath}");
            _mainWindow.SetStatus(
                $"ETW candidates captured for hardware review. They cannot unlock ASUS writes or clear recovery state: {string.Join(" ", result.AssessmentReasons)}");
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{result.BundlePath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            if (ex is ArmouryCaptureTeardownException ||
                (session?.UsesArmouryTap == true && ex is not OperationCanceledException))
            {
                captureTeardownFailure = ex;
            }
            var failureMessage = ex.Message;
            if (session is not null)
            {
                try
                {
                    session.CancelAndDelete();
                }
                catch (Exception cleanupFailure)
                {
                    captureTeardownFailure = cleanupFailure;
                    failureMessage = $"{failureMessage}\n\nPRIVACY WARNING: {cleanupFailure.Message}";
                }
            }
            _mainWindow.SetArmouryCaptureStatus(
                ex is OperationCanceledException
                    ? $"Capture cancelled safely: {failureMessage}"
                    : $"Capture failed safely: {failureMessage}");
            if (ex is not OperationCanceledException)
            {
                deferredFailureMessage = failureMessage;
                deferredDiagnostic = ex as ArmouryCaptureException;
                deferredDiagnosticText = deferredDiagnostic?.DiagnosticText;
            }
        }
        finally
        {
            TaskCompletionSource? captureCompletion;
            await _operationGate.WaitAsync();
            try
            {
                _mainWindow.SetArmouryCaptureBusy(false);
                _armouryCaptureCancellation?.Dispose();
                _armouryCaptureCancellation = null;
                captureCompletion = _armouryCaptureCompletion;
                if (captureTeardownFailure is null)
                {
                    _armouryCaptureInProgress = false;
                    _armouryCaptureCompletion = null;
                }
                else
                {
                    _armouryCaptureTeardownUnconfirmed = true;
                    Configuration = Configuration with
                    {
                        ArmouryTapTeardownBlockedSinceUtc =
                            Configuration.ArmouryTapTeardownBlockedSinceUtc ?? DateTimeOffset.UtcNow,
                        ArmouryTapTeardownBootIdentifier =
                            Configuration.ArmouryTapTeardownBootIdentifier ?? TryGetCurrentBootIdentifier(),
                    };
                    try { await _profileStore.SaveAsync(Configuration); }
                    catch (Exception persistenceFailure)
                    {
                        captureTeardownFailure = new AggregateException(captureTeardownFailure, persistenceFailure);
                    }
                }
            }
            finally
            {
                _operationGate.Release();
            }
            if (captureTeardownFailure is null)
            {
                captureCompletion?.TrySetResult();
            }
            else
            {
                captureCompletion?.TrySetException(new InvalidOperationException(
                    "The native tap unload could not be confirmed. Native controller resets remain blocked until Windows restarts.",
                    captureTeardownFailure));
                _mainWindow.SetArmouryCaptureStatus(
                    "CAPTURE TEARDOWN UNCONFIRMED — native controller resets are blocked. Restart Windows before continuing.");
            }
        }
        if (deferredFailureMessage is not null && !cancellationToken.IsCancellationRequested)
        {
            var copyDiagnostics = await _mainWindow.ShowControllerDialogAsync(
                "Armoury capture failed safely",
                $"No Ally Bindings controller write was attempted.\n\n{deferredFailureMessage}" +
                (deferredDiagnostic is null
                    ? string.Empty
                    : $"\n\nDiagnostic: {deferredDiagnostic.DiagnosticPath}\n\n" +
                      "It contains bounded lifecycle stages, process/elevation state, product version, helper exit code, and redacted error types/codes/messages. " +
                      "It contains no usernames, absolute paths, stack traces, USB payloads, controller reports, configuration values, or raw ETW data.\n\n" +
                      "Choose Copy diagnostics to place that JSON on the clipboard, or Open folder to attach the file."),
                allowCancel: deferredDiagnostic is not null,
                primaryLabel: deferredDiagnostic is null ? "OK" : "Copy diagnostics",
                secondaryLabel: "Open folder");
            if (deferredDiagnostic is not null)
            {
                var copied = false;
                if (copyDiagnostics && !string.IsNullOrWhiteSpace(deferredDiagnosticText))
                {
                    try
                    {
                        System.Windows.Clipboard.SetText(deferredDiagnosticText);
                        copied = true;
                    }
                    catch
                    {
                        // Fall through to Explorer when another process owns the clipboard.
                    }
                }
                if (!copied)
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = $"/select,\"{deferredDiagnostic.DiagnosticPath}\"",
                            UseShellExecute = true,
                        });
                    }
                    catch (Exception disclosureError)
                    {
                        _mainWindow.SetArmouryCaptureStatus(
                            $"Diagnostic saved at {deferredDiagnostic.DiagnosticPath}, but Windows could not open Explorer: {disclosureError.Message}");
                    }
                }
            }
        }
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
        _mainWindow.SetUpdateBusy(true);
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
                _mainWindow.SetUpdateStatus($"Current version {GitHubUpdateService.CurrentSemanticVersion} is up to date.");
                return;
            }

            _mainWindow.SetUpdateStatus($"Update available: {candidate.TagName}");
            var choice = await _mainWindow.ShowControllerDialogAsync(
                "Ally Bindings update",
                $"{candidate.ReleaseName} is available.\n\n" +
                "The ZIP will be downloaded from this repository, verified against GitHub's SHA-256 digest, staged safely, then installed after Ally Bindings exits.\n\n" +
                "Install and restart now?",
                primaryLabel: "Install");
            if (!choice) return;

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
                await _mainWindow.ShowControllerDialogAsync(
                    "Ally Bindings update",
                    $"The update was not installed. Existing app files were not changed.\n\n{ex.Message}",
                    allowCancel: false,
                    primaryLabel: "OK");
            }
        }
        finally
        {
            _updateCheckInProgress = false;
            _mainWindow.SetUpdateBusy(false);
        }
    }

    private async Task<bool> ConfirmSafeExitForUpdateAsync()
    {
        await using var resetLease = await CaptureResetGate.AcquireWhenCaptureStoppedAsync(
            _operationGate,
            () => _armouryCaptureInProgress ? _armouryCaptureCompletion?.Task : null,
            RequestArmouryCaptureCancellation);
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

        var continueWithoutReset = await _mainWindow.ShowControllerDialogAsync(
            "Controller recovery not confirmed",
            "The best-known native M1/M2 reset could not be written. Updating now may leave the last paddle mapping active until Armoury Crate or another recovery path overwrites it.\n\nUpdate anyway?",
            primaryLabel: "Update anyway",
            secondaryLabel: "Cancel update");
        _allowExitWithPendingRearMapping = continueWithoutReset;
        return continueWithoutReset;
    }

    private async Task<BackendStatus> ReplaceBackendAsync(
        bool enableRearButtons,
        bool restoreCurrent,
        bool allowUnverifiedRecoveryReset = false)
    {
        var useAsusBackend =
            (enableRearButtons && ArmouryProtocolValidation.IsOperationApproved(isRecoveryReset: false)) ||
            (allowUnverifiedRecoveryReset && ArmouryProtocolValidation.RecoveryWritesApproved);
        IControllerBackend replacement = useAsusBackend
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
        _mainWindow.FocusControllerDefault();
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        IAsyncDisposable? exitLease = null;
        try
        {
            exitLease = await CaptureResetGate.AcquireWhenCaptureStoppedAsync(
                _operationGate,
                () => _armouryCaptureInProgress ? _armouryCaptureCompletion?.Task : null,
                RequestArmouryCaptureCancellation);
        }
        catch (Exception ex) when (_armouryCaptureTeardownUnconfirmed)
        {
            var exitWithoutReset = await _mainWindow.ShowControllerDialogAsync(
                "ETW capture teardown unconfirmed",
                "A native tap unload was not confirmed, so Ally Bindings will not issue any controller reset or backend shutdown write. The fail-closed barrier is persisted across app restarts.\n\n" +
                $"Details: {ex.Message}\n\nExit without reset now? Restart Windows before reopening Ally Bindings or using native controller writes.",
                primaryLabel: "Exit without reset",
                secondaryLabel: "Stay open");
            if (!exitWithoutReset)
            {
                _exiting = false;
                OpenMainWindow();
                return;
            }
            _backendDisposed = true;
            Shutdown();
            return;
        }
        var shouldShutdown = false;

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
                var exitAnyway = await _mainWindow.ShowControllerDialogAsync(
                    "Controller recovery not confirmed",
                    "The best-known native M1/M2 reset failed. Exiting may leave the last paddle mapping active until Armoury Crate or another recovery path overwrites it.\n\n" +
                    $"Details: {restoreFailure ?? "No interface accepted the reset."}\n\nExit anyway?",
                    primaryLabel: "Exit anyway",
                    secondaryLabel: "Stay open");
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
            StopActivationListener();
            TryCleanup(() => _mainWindow.AllowClose());
            TryCleanup(() => _mainWindow.Close());
            ReleaseSingleInstanceMutex();
            shouldShutdown = true;
        }
        catch (Exception ex)
        {
            _exiting = false;
            _mainWindow.SetStatus($"Exit blocked because recovery confirmation could not complete: {ex.Message}");
            OpenMainWindow();
        }
        finally
        {
            if (exitLease is not null) await exitLease.DisposeAsync();
            if (shouldShutdown) Shutdown();
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _exiting = true;
        RestoreAndDisposeForTermination();
        base.OnSessionEnding(e);
    }

    private void RequestArmouryCaptureCancellation()
    {
        _armouryCaptureCancellation?.Cancel();
        if (_mainWindow.Dispatcher.CheckAccess())
        {
            _mainWindow.CancelControllerDialog();
            return;
        }
        _mainWindow.Dispatcher.Invoke(_mainWindow.CancelControllerDialog);
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
        StopActivationListener();
        TryCleanup(() => _executableIntegrityLock?.Dispose());
        _executableIntegrityLock = null;
        ReleaseSingleInstanceMutex();
        base.OnExit(e);
    }

    private void RestoreAndDisposeForTermination()
    {
        if (_armouryCaptureInProgress || _armouryCaptureTeardownUnconfirmed)
        {
            // Never overlap a possibly-live elevated ETW session with backend writes.
            // Process exit closes the authenticated pipe; the helper then tears down.
            _backendDisposed = true;
            return;
        }
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

    private static Guid? TryGetCurrentBootIdentifier()
    {
        var information = new SystemBootEnvironmentInformation();
        var status = NtQuerySystemInformation(90, ref information,
            Marshal.SizeOf<SystemBootEnvironmentInformation>(), out _);
        return status == 0 && information.BootIdentifier != Guid.Empty
            ? information.BootIdentifier
            : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemBootEnvironmentInformation
    {
        public Guid BootIdentifier;
        public int FirmwareType;
        public ulong BootFlags;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int informationClass,
        ref SystemBootEnvironmentInformation information, int informationLength, out int returnLength);
}
