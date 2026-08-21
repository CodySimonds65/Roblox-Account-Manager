using System.Globalization;
using RobloxAccountManager.Core.Launch;

namespace RobloxAccountManager.Desktop.Services;

public static class MacUpdateActivityFormatter
{
    public static string FormatUnsignedValidationRejection(
        string validationError,
        ulong currentPackageVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(validationError);
        return $"Unsigned update rejected before prompt: {LaunchDiagnostics.SanitiseCode(validationError)} " +
               $"(installed pkg version: {currentPackageVersion.ToString(CultureInfo.InvariantCulture)}).";
    }
}
