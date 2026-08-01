using Microsoft.Win32;

namespace AllyBindings.Windows;

public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AllyBindings";

    public static string? CurrentCommand
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) as string;
        }
    }

    public static string ExpectedCommand
    {
        get
        {
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("Unable to locate Ally Bindings executable.");
            return $"\"{executable}\" --background";
        }
    }

    public static bool IsEnabled() =>
        string.Equals(CurrentCommand, ExpectedCommand, StringComparison.OrdinalIgnoreCase);

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
        {
            key.SetValue(ValueName, ExpectedCommand, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
