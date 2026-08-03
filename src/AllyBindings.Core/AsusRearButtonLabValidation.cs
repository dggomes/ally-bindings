namespace AllyBindings.Core;

/// <summary>
/// Fixed, deliberately narrow policy for the private physical M1/M2 lab test.
/// It does not alter the application's hardware-write approval gates.
/// </summary>
public static class AsusRearButtonLabValidation
{
    public const string InspectCommand = "inspect";
    public const string WriteCommand = "write-m1-a-m2-b";
    public const string ConfirmationPhrase = "I SAVED SETTINGS; WRITE M1=A M2=B";

    public static byte[] BuildOneShotReport() =>
        AsusRearButtonProtocol.BuildMappingReport(ControllerButton.A, ControllerButton.B);

    public static bool IsExactInterfaceSnapshot(
        IReadOnlyList<string> expected,
        IReadOnlyList<string> current) =>
        expected.Count > 0 && current.SequenceEqual(expected, StringComparer.Ordinal);

    public static AsusRearButtonLabAuthorization Authorize(
        string? command,
        string? confirmation,
        bool inputRedirected,
        int compatibleInterfaceCount)
    {
        if (!string.Equals(command, WriteCommand, StringComparison.Ordinal))
        {
            return new(false, "The fixed one-shot write command was not selected.");
        }

        if (inputRedirected)
        {
            return new(false, "Refusing a hardware write without an interactive console.");
        }

        if (compatibleInterfaceCount != 1)
        {
            return new(
                false,
                $"Refusing an ambiguous hardware write: expected exactly one compatible interface, found {compatibleInterfaceCount}.");
        }

        if (!string.Equals(confirmation, ConfirmationPhrase, StringComparison.Ordinal))
        {
            return new(false, "The typed confirmation did not match exactly; no hardware write was attempted.");
        }

        return new(true, "The fixed one-shot M1=A/M2=B lab write is authorized.");
    }
}

public sealed record AsusRearButtonLabAuthorization(bool Approved, string Message);
