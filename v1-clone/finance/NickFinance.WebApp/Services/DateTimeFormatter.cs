namespace NickFinance.WebApp.Services;

/// <summary>
/// Centralised date/time formatter. Converts every <see cref="DateTimeOffset"/>
/// surfaced in the UI to Africa/Accra (UTC+0 today, but the wrapper means
/// future DST or zone migrations cost nothing) and provides a relative-time
/// helper for "3 minutes ago / yesterday / in 2 days" stamps.
///
/// Resolution strategy for the timezone: try Windows id "GMT Standard Time"
/// first; fall back to IANA "Africa/Accra" (Linux); fall back to UTC. Each
/// environment will succeed on the first applicable id.
/// </summary>
public static class DateTimeFormatter
{
    /// <summary>Africa/Accra — resolved once, cached for the process lifetime.</summary>
    public static readonly TimeZoneInfo Accra = ResolveAccra();

    private static TimeZoneInfo ResolveAccra()
    {
        // Windows-style id first. Server2022 + most NickFinance hosts.
        try { return TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"); }
        catch (TimeZoneNotFoundException) { }
        catch (InvalidTimeZoneException) { }

        // IANA — Linux / containers / .NET on Mac.
        try { return TimeZoneInfo.FindSystemTimeZoneById("Africa/Accra"); }
        catch (TimeZoneNotFoundException) { }
        catch (InvalidTimeZoneException) { }

        // Last-resort fallback. Ghana is UTC+0 with no DST so this is a
        // semantically correct fallback even if the friendly id is gone.
        return TimeZoneInfo.Utc;
    }

    /// <summary>"yyyy-MM-dd HH:mm" in Africa/Accra. Good for tables.</summary>
    public static string Local(DateTimeOffset dto)
        => TimeZoneInfo.ConvertTime(dto, Accra).ToString("yyyy-MM-dd HH:mm");

    /// <summary>"yyyy-MM-dd" in Africa/Accra. For date-only columns.</summary>
    public static string LocalDate(DateTimeOffset dto)
        => TimeZoneInfo.ConvertTime(dto, Accra).ToString("yyyy-MM-dd");

    /// <summary>"HH:mm" in Africa/Accra. For when the date is implicit.</summary>
    public static string LocalTime(DateTimeOffset dto)
        => TimeZoneInfo.ConvertTime(dto, Accra).ToString("HH:mm");

    /// <summary>
    /// "3 minutes ago", "yesterday at 14:00", "in 2 days" — relative to now.
    /// Returns absolute local stamp for things older than 7 days or further
    /// than 7 days in the future.
    /// </summary>
    public static string Relative(DateTimeOffset dto, DateTimeOffset? nowOverride = null)
    {
        var now = nowOverride ?? DateTimeOffset.UtcNow;
        var diff = now - dto;
        var future = diff < TimeSpan.Zero;
        var abs = future ? -diff : diff;

        // Just now
        if (abs.TotalSeconds < 30) return "just now";

        // < 1 minute
        if (abs.TotalSeconds < 60)
        {
            var sec = (int)abs.TotalSeconds;
            return future ? $"in {sec} seconds" : $"{sec} seconds ago";
        }
        // < 1 hour
        if (abs.TotalMinutes < 60)
        {
            var min = (int)abs.TotalMinutes;
            return future
                ? $"in {min} minute{(min == 1 ? "" : "s")}"
                : $"{min} minute{(min == 1 ? "" : "s")} ago";
        }

        // Yesterday / today / tomorrow as anchors
        var localDto = TimeZoneInfo.ConvertTime(dto, Accra);
        var localNow = TimeZoneInfo.ConvertTime(now, Accra);
        var dtoDate = localDto.Date;
        var nowDate = localNow.Date;
        var dayDiff = (dtoDate - nowDate).Days;

        if (dayDiff == 0)
        {
            var hr = (int)abs.TotalHours;
            return future
                ? $"in {hr} hour{(hr == 1 ? "" : "s")}"
                : $"{hr} hour{(hr == 1 ? "" : "s")} ago";
        }
        if (dayDiff == -1) return $"yesterday at {localDto:HH:mm}";
        if (dayDiff == 1)  return $"tomorrow at {localDto:HH:mm}";

        if (Math.Abs(dayDiff) <= 7)
        {
            return future
                ? $"in {dayDiff} days"
                : $"{-dayDiff} days ago";
        }

        // Beyond a week — fall back to absolute local stamp.
        return Local(dto);
    }
}
