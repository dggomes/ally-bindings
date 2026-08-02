using AllyBindings.Core;

namespace AllyBindings.Core.Tests;

public sealed class AsusAllyModelIdentityTests
{
    [Theory]
    [InlineData("RC71L")]
    [InlineData("RC72LA")]
    [InlineData("RC73XA")]
    [InlineData("RC73YA")]
    [InlineData("rc73xa")]
    [InlineData(" RC73XA ")]
    [InlineData("RC73XA_RC73XA")]
    [InlineData("rc73xa_RC73XA")]
    public void Recognizes_only_positive_Ally_product_names(string productName)
    {
        Assert.True(AsusAllyModelIdentity.IsSupportedProductName(productName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("OTHER")]
    [InlineData("RC73XA_OTHER")]
    [InlineData("RC73XA_RC73YA")]
    [InlineData("ROG-RC73XA")]
    [InlineData("RC73XA_EXTRA_RC73XA")]
    [InlineData("RC73XA_")]
    [InlineData("_RC73XA")]
    public void Rejects_ambiguous_or_unrelated_product_names(string? productName)
    {
        Assert.False(AsusAllyModelIdentity.IsSupportedProductName(productName));
    }
}
