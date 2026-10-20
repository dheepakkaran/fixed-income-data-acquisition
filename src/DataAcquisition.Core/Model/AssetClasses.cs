namespace DataAcquisition.Core.Model;

/// <summary>
/// Asset class labels. A string rather than an enum so that adding an asset class
/// is a provider-side change and never a Core change.
/// </summary>
public static class AssetClasses
{
    public const string AutoLoanAbs = "AutoLoanABS";
    public const string UsTreasuries = "USTreasuries";
}
