using System.Text.Json;
using AllyBindings.SoftwareProbe;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace AllyBindings.M1M2Probe;

internal static class Program
{
    private const int Success = 0;
    private const int Failure = 1;
    private const int Usage = 64;

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
            {
                PrintUsage();
                return args.Length == 0 ? Usage : Success;
            }

            return args[0] switch
            {
                "inspect" => Inspect(args),
                "self-test" => SelfTest(args),
                "start" => Start(args),
                "listen" => Listen(args),
                "emit-f17" => Emit("F17", args),
                "emit-f18" => Emit("F18", args),
                "bridge" => Bridge(args),
                "checkpoint" => Checkpoint(args),
                "finalize" => Finalize(args),
                _ => UnknownCommand(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR — {ex.Message}");
            return Failure;
        }
    }

    private static int Inspect(string[] args)
    {
        ValidateArguments(args, [], []);
        var capabilities = WindowsCapabilities.Inspect();
        Console.WriteLine(JsonSerializer.Serialize(capabilities, JsonOptions));
        Console.WriteLine();
        Console.WriteLine("READ-ONLY — no ASUS HID access, driver install or device hiding occurred.");
        return Success;
    }

    private static int SelfTest(string[] args)
    {
        ValidateArguments(args, [], []);
        WindowsCapabilities.EnsureWindows();
        var size = F17F18KeyboardHook.ValidateSendInputLayout();
        Console.WriteLine($"SELF-TEST PASSED — Win32 INPUT layout is {size} bytes.");
        return Success;
    }

    private static int Start(string[] args)
    {
        ValidateArguments(args, ["--root"], []);
        var root = Option(args, "--root") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AllyBindings",
            "software-probe");
        var session = SoftwareProbeSession.Create(WindowsCapabilities.Inspect(), DateTimeOffset.UtcNow);
        var directory = SoftwareProbeEvidenceStore.CreateSessionDirectory(root, session);
        Console.WriteLine(directory);
        Console.WriteLine("Session created in software-only safety mode.");
        return Success;
    }

    private static int Listen(string[] args)
    {
        ValidateArguments(args, ["--session", "--seconds"], ["--suppress"]);
        var directory = RequiredOption(args, "--session");
        var seconds = IntegerOption(args, "--seconds", 30, 1, 1_800);
        var suppress = HasFlag(args, "--suppress");
        using var captureLease = SoftwareProbeEvidenceStore.AcquireCaptureLease(directory);
        var journal = new EvidenceJournal(directory);

        Console.WriteLine($"Listening only for F17/F18 for {seconds} seconds. Suppression: {suppress}.");
        Console.WriteLine("All other keyboard input is ignored and never retained.");
        using var hook = new F17F18KeyboardHook(
            suppress,
            suppress ? "capture-suppress" : "capture-observe",
            keyEvent =>
            {
                journal.Add(keyEvent);
                Console.WriteLine($"{keyEvent.TimestampUtc:O} {keyEvent.Key} {(keyEvent.IsKeyDown ? "down" : "up")} injected={keyEvent.IsInjected} suppressed={keyEvent.WasSuppressed}");
            });
        RunHookWithCtrlC(hook, TimeSpan.FromSeconds(seconds));
        Console.WriteLine($"Captured {journal.Session.KeyEvents.Length} F17/F18 events in this session.");
        return Success;
    }

    private static int Emit(string key, string[] args)
    {
        ValidateArguments(args, ["--delay"], []);
        var delay = IntegerOption(args, "--delay", 3, 0, 30);
        Console.WriteLine($"Focus Armoury Crate's keyboard-assignment field now. Emitting one {key} press/release in {delay} seconds.");
        F17F18KeyboardHook.Emit(key, TimeSpan.FromSeconds(delay));
        Console.WriteLine($"Emitted {key}. No ASUS HID interface was opened.");
        return Success;
    }

    private static int Bridge(string[] args)
    {
        ValidateArguments(args, ["--session", "--seconds"], []);
        var directory = RequiredOption(args, "--session");
        var seconds = IntegerOption(args, "--seconds", 120, 1, 1_800);
        using var captureLease = SoftwareProbeEvidenceStore.AcquireCaptureLease(directory);
        var journal = new EvidenceJournal(directory);
        var currentCapabilities = WindowsCapabilities.Inspect();
        if (!currentCapabilities.ViGEmBusInstalled || currentCapabilities.ViGEmBusStatus != "Running")
            throw new InvalidOperationException($"ViGEmBus is not currently running ({currentCapabilities.ViGEmBusStatus}). The probe will not install or start a driver automatically.");

        Console.WriteLine("Creating one temporary virtual Xbox 360 controller.");
        Console.WriteLine("F18/M1 -> A, F17/M2 -> B. Both source keys are suppressed while this command runs.");
        Console.WriteLine("The physical controller is NOT hidden. Use ASUS Command Center to disable it for the virtual-only test.");

        using var client = new ViGEmClient();
        var controller = client.CreateXbox360Controller();
        Exception? bridgeFailure = null;
        var cleanupFailures = new List<Exception>();
        var cleanupStarted = 0;
        void Cleanup()
        {
            if (Interlocked.Exchange(ref cleanupStarted, 1) != 0) return;
            TryCleanup(() => controller.SetButtonState(Xbox360Button.A, false), cleanupFailures);
            TryCleanup(() => controller.SetButtonState(Xbox360Button.B, false), cleanupFailures);
            TryCleanup(controller.Disconnect, cleanupFailures);
        }
        EventHandler processExit = (_, _) => Cleanup();
        AppDomain.CurrentDomain.ProcessExit += processExit;
        try
        {
            controller.Connect();
            using var hook = new F17F18KeyboardHook(
                suppress: true,
                mode: "virtual-bridge",
                eventSink: keyEvent =>
                {
                    journal.Add(keyEvent);
                    Console.WriteLine($"{keyEvent.Key} {(keyEvent.IsKeyDown ? "down" : "up")} -> {(keyEvent.Key == "F18" ? "A" : "B")} injected={keyEvent.IsInjected}");
                },
                keyStateSink: (key, down, injected) =>
                {
                    if (injected) return;
                    controller.SetButtonState(key == "F18" ? Xbox360Button.A : Xbox360Button.B, down);
                });
            RunHookWithCtrlC(hook, TimeSpan.FromSeconds(seconds));
        }
        catch (Exception exception)
        {
            bridgeFailure = exception;
        }
        finally
        {
            Cleanup();
            AppDomain.CurrentDomain.ProcessExit -= processExit;
        }

        if (bridgeFailure is not null || cleanupFailures.Count != 0)
        {
            var failures = new List<Exception>();
            if (bridgeFailure is not null) failures.Add(bridgeFailure);
            failures.AddRange(cleanupFailures);
            throw new AggregateException("The bridge or its fail-safe cleanup failed.", failures);
        }

        Console.WriteLine("Virtual controller disconnected cleanly; A and B were released first.");
        return Success;
    }

    private static int Checkpoint(string[] args)
    {
        ValidateArguments(args, ["--session", "--name", "--result"], []);
        var directory = RequiredOption(args, "--session");
        var name = RequiredOption(args, "--name");
        var resultText = RequiredOption(args, "--result");
        var result = resultText.ToLowerInvariant() switch
        {
            "pass" => SoftwareProbeCheckpointResult.Pass,
            "fail" => SoftwareProbeCheckpointResult.Fail,
            "skipped" => SoftwareProbeCheckpointResult.Skipped,
            "unknown" => SoftwareProbeCheckpointResult.Unknown,
            _ => throw new ArgumentOutOfRangeException("--result", "Result must be pass, fail, skipped or unknown."),
        };
        var journal = new EvidenceJournal(directory);
        journal.SetCheckpoint(name, result);
        Console.WriteLine($"Recorded {name}: {result}.");
        return Success;
    }

    private static int Finalize(string[] args)
    {
        ValidateArguments(args, ["--session", "--out"], []);
        var directory = RequiredOption(args, "--session");
        var destination = Option(args, "--out");
        var result = SoftwareProbeEvidenceStore.FinalizeBundle(directory, destination);
        Console.WriteLine(result.ZipPath);
        Console.WriteLine($"SHA-256: {result.Sha256}");
        return Success;
    }

    private static void TryCleanup(Action action, ICollection<Exception> failures)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static void RunHookWithCtrlC(F17F18KeyboardHook hook, TimeSpan duration)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            hook.Run(duration, cancellation.Token);
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintUsage();
        return Usage;
    }

    private static string RequiredOption(string[] args, string name) =>
        Option(args, name) ?? throw new ArgumentException($"Missing required option {name}.");

    private static string? Option(string[] args, string name)
    {
        for (var index = 1; index < args.Length; index++)
        {
            if (!string.Equals(args[index], name, StringComparison.Ordinal)) continue;
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Option {name} requires a value.");
            return args[index + 1];
        }
        return null;
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Skip(1).Any(argument => string.Equals(argument, name, StringComparison.Ordinal));

    private static int IntegerOption(string[] args, string name, int defaultValue, int minimum, int maximum)
    {
        var text = Option(args, name);
        if (text is null) return defaultValue;
        if (!int.TryParse(text, out var value) || value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(name, $"{name} must be between {minimum} and {maximum}.");
        return value;
    }

    private static void ValidateArguments(string[] args, IReadOnlyCollection<string> valueOptions, IReadOnlyCollection<string> flags)
    {
        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];
            if (flags.Contains(argument, StringComparer.Ordinal)) continue;
            if (!valueOptions.Contains(argument, StringComparer.Ordinal))
                throw new ArgumentException($"Unknown option or extra argument: {argument}");
            if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Option {argument} requires a value.");
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Ally Bindings M1/M2 software probe — no ASUS HID writes; no driver installation; no device hiding");
        Console.WriteLine();
        Console.WriteLine("  inspect");
        Console.WriteLine("  self-test");
        Console.WriteLine("  start [--root <directory>]");
        Console.WriteLine("  listen --session <directory> [--seconds 30] [--suppress]");
        Console.WriteLine("  emit-f17 [--delay 3]");
        Console.WriteLine("  emit-f18 [--delay 3]");
        Console.WriteLine("  bridge --session <directory> [--seconds 120]");
        Console.WriteLine("  checkpoint --session <dir> --name <checkpoint> --result <pass|fail|skipped|unknown>");
        Console.WriteLine("  finalize --session <directory> [--out <zip-path>]");
        Console.WriteLine();
        Console.WriteLine("Checkpoint names:");
        foreach (var name in SoftwareProbeCheckpoints.Allowed.OrderBy(value => value)) Console.WriteLine($"  {name}");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}

internal sealed class EvidenceJournal
{
    private readonly object _sync = new();
    private readonly string _directory;

    internal EvidenceJournal(string directory)
    {
        _directory = directory;
        Session = SoftwareProbeEvidenceStore.Load(directory);
    }

    internal SoftwareProbeSession Session { get; private set; }

    internal void Add(SoftwareProbeKeyEvent keyEvent)
    {
        lock (_sync)
        {
            Session = SoftwareProbeEvidenceStore.Update(_directory, current => current.AddKeyEvent(keyEvent));
        }
    }

    internal void SetCheckpoint(string name, SoftwareProbeCheckpointResult result)
    {
        lock (_sync)
        {
            Session = SoftwareProbeEvidenceStore.Update(
                _directory,
                current => current.SetCheckpoint(name, result, DateTimeOffset.UtcNow));
        }
    }
}
