namespace AllyBindings.Core;

/// <summary>
/// Positively identifies ASUS ROG Ally DMI product names without accepting
/// arbitrary prefixes or suffixes. Some Ally firmware repeats the model token
/// on both sides of an underscore (for example RC73XA_RC73XA).
/// </summary>
public static class AsusAllyModelIdentity
{
    private static readonly string[] SupportedModels = ["RC71L", "RC72LA", "RC73XA", "RC73YA"];

    public static bool IsSupportedProductName(string? productName)
    {
        if (string.IsNullOrWhiteSpace(productName)) return false;

        var tokens = productName.Trim().Split('_');
        if (tokens.Length is < 1 or > 2) return false;

        var model = tokens[0];
        if (!SupportedModels.Contains(model, StringComparer.OrdinalIgnoreCase)) return false;

        return tokens.Length == 1 || tokens[1].Equals(model, StringComparison.OrdinalIgnoreCase);
    }
}
