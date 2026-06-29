namespace SmartReviewSystem.Services.DevOps;

/// <summary>
/// Utility class for formatting and tracking implementation time metrics.
/// </summary>
internal static class ImplementationTimeHelper
{
    /// <summary>
    /// Formats a TimeSpan into a human-readable duration string.
    /// Examples: "2d 3h 15m", "5h 30m", "45m", "2d"
    /// </summary>
    public static string FormatDuration(TimeSpan? duration)
    {
        if (duration is null)
        {
            return "-";
        }

        var ts = duration.Value;

        if (ts.TotalSeconds < 60)
        {
            return $"{(int)ts.TotalSeconds}s";
        }

        if (ts.TotalMinutes < 60)
        {
            return $"{(int)ts.TotalMinutes}m";
        }

        if (ts.TotalHours < 24)
        {
            return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        }

        var days = ts.Days;
        var hours = ts.Hours;
        var minutes = ts.Minutes;

        if (minutes == 0)
        {
            return hours == 0 ? $"{days}d" : $"{days}d {hours}h";
        }

        return hours == 0 ? $"{days}d {minutes}m" : $"{days}d {hours}h {minutes}m";
    }

    /// <summary>
    /// Gets the total hours as a decimal for sorting or calculations.
    /// </summary>
    public static double GetTotalHours(TimeSpan? duration)
    {
        return duration?.TotalHours ?? 0;
    }

    /// <summary>
    /// Gets the total days rounded to 1 decimal place.
    /// </summary>
    public static decimal GetTotalDays(TimeSpan? duration)
    {
        return duration is null ? 0 : (decimal)duration.Value.TotalDays;
    }

    /// <summary>
    /// Formats a DateTimeOffset to local time with consistent formatting.
    /// Converts UTC timestamps from Azure DevOps to local time for display.
    /// Returns "-" if the value is null.
    /// </summary>
    public static string FormatDateTimeOffset(DateTimeOffset? dateTime)
    {
        if (dateTime is null)
        {
            return "-";
        }

        // Convert UTC timestamp to local time and format consistently
        var localTime = dateTime.Value.ToLocalTime();
        return localTime.ToString("g");
    }

    /// <summary>
    /// Categorizes implementation time into buckets for reporting.
    /// </summary>
    public static string CategorizeDuration(TimeSpan? duration)
    {
        if (duration is null)
        {
            return "No Data";
        }

        var hours = duration.Value.TotalHours;

        return hours switch
        {
            < 1 => "< 1 hour",
            < 4 => "1-4 hours",
            < 8 => "4-8 hours",
            < 24 => "1 day",
            < 48 => "1-2 days",
            < 72 => "2-3 days",
            < 168 => "3-7 days",
            _ => "> 1 week"
        };
    }
}
