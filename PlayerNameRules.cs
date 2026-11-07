using System.Text.RegularExpressions;

namespace IMCBedwarsOverlay;

internal static class PlayerNameRules
{
    public const string Pattern = @"-?[A-Za-z0-9_]{3,16}";
    static readonly Regex Validator = new($"^{Pattern}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsValid(string? name) => name is not null && Validator.IsMatch(name);

    public static string PreserveRequestedIdentity(string requestedName, string? apiName)
        => requestedName.StartsWith("-", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(apiName)
            ? requestedName
            : apiName;
}
