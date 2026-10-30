using System.Globalization;
using System.Text.RegularExpressions;
using Content.Shared.Dataset;
using Robust.Shared.Prototypes;

namespace Content.Shared.Players.PlayTimeTracking;

public static class PlayTimeTrackingShared
{
    /// <summary>
    /// The prototype ID of the play time tracker that represents overall playtime, i.e. not tied to any one role.
    /// </summary>
    public static readonly ProtoId<PlayTimeTrackerPrototype> TrackerOverall = "Overall";

    /// <summary>
    /// The prototype ID of the play time tracker that represents admin time, when a player is in game as admin.
    /// </summary>
    public static readonly ProtoId<PlayTimeTrackerPrototype> TrackerAdmin = "Admin";

    public static readonly TimeSpan MaxAdminTime = TimeSpan.FromDays(365 * 100);

    private static readonly Dictionary<string, int> UnitMinutes = new()
    {
        { "y", 525960 },
        { "mo", 43800 },
        { "w", 10080 },
        { "d", 1440 },
        { "h", 60 },
        { "m", 1 },
    };

    private static readonly Regex TimeText = new(@"^(\d+\s*(y|mo|w|d|h|m)\s*)+$", RegexOptions.IgnoreCase);
    private static readonly Regex TimePart = new(@"(\d+)\s*(y|mo|w|d|h|m)", RegexOptions.IgnoreCase);

    public static bool TryParseTime(string text, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        text = text.Trim();

        var negative = text.StartsWith('-');
        if (negative)
            text = text[1..].TrimStart();

        double minutes = 0;
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var plain))
        {
            minutes = plain;
        }
        else
        {
            if (!TimeText.IsMatch(text))
                return false;

            foreach (Match match in TimePart.Matches(text))
            {
                if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
                    return false;

                minutes += (double) amount * UnitMinutes[match.Groups[2].Value.ToLowerInvariant()];
            }
        }

        if (minutes > MaxAdminTime.TotalMinutes)
            return false;

        time = TimeSpan.FromMinutes(negative ? -minutes : minutes);
        return true;
    }
}
