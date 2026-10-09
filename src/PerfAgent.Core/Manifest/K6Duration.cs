using System.Globalization;
using System.Text.RegularExpressions;

namespace PerfAgent.Core.Manifest;

/// <summary>Duraciones con la sintaxis de k6 (<c>30s</c>, <c>2m</c>, <c>1m30s</c>, <c>1h</c>).</summary>
public static partial class K6Duration
{
    public static bool TryParseSeconds(string? text, out int seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = DurationRegex().Match(text.Trim());
        if (!match.Success || match.Length == 0)
        {
            return false;
        }

        long total = 0;
        if (match.Groups["h"].Success)
        {
            total += long.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture) * 3600;
        }

        if (match.Groups["m"].Success)
        {
            total += long.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) * 60;
        }

        if (match.Groups["s"].Success)
        {
            total += long.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture);
        }

        if (total > int.MaxValue)
        {
            return false;
        }

        seconds = (int)total;
        return true;
    }

    public static string Format(int seconds) => seconds % 60 == 0 && seconds > 0
        ? string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}m")
        : string.Create(CultureInfo.InvariantCulture, $"{seconds}s");

    [GeneratedRegex(@"^(?:(?<h>\d{1,4})h)?(?:(?<m>\d{1,5})m)?(?:(?<s>\d{1,6})s)?$")]
    private static partial Regex DurationRegex();
}
