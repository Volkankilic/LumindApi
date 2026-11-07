using System.Globalization;
using System.Text;

namespace Chatbot.Application.Admin;

/// <summary>
/// The arithmetic of the statistics, with no database and no text. Everything here works on times and counts.
/// Definitions (also in docs/yonetim-kilavuzu.md):
/// <list type="bullet">
/// <item><b>Estimated active time</b> of a conversation: the sum of the gaps between consecutive messages that are SHORTER than the
/// threshold (default 10 minutes). It is an estimate: reading time without typing and time away are not seen, and a single
/// message has no gap at all. No heartbeat or screen tracking exists.</item>
/// <item><b>State:</b> Completed = it has an end; Abandoned = no end and no activity for longer than the threshold (default 24 hours);
/// Ongoing = no end and recent activity.</item>
/// </list>
/// </summary>
public static class AdminStatsMath
{
    /// <summary>Sum of the gaps strictly shorter than <paramref name="maxGap"/> between consecutive times (sorted here, so the input order does not matter).</summary>
    public static TimeSpan EstimatedActiveTime(IEnumerable<DateTimeOffset> messageTimes, TimeSpan maxGap)
    {
        var sorted = messageTimes.OrderBy(t => t).ToList();
        var total = TimeSpan.Zero;
        for (var i = 1; i < sorted.Count; i++)
        {
            var gap = sorted[i] - sorted[i - 1];
            if (gap < maxGap)
            {
                total += gap;
            }
        }

        return total;
    }

    /// <param name="lastActivity">The last message, or the start when there is none.</param>
    public static ConversationState StateOf(DateTimeOffset? endedAt, DateTimeOffset lastActivity, DateTimeOffset now, TimeSpan abandonedAfter)
    {
        if (endedAt is not null)
        {
            return ConversationState.Completed;
        }

        return now - lastActivity > abandonedAfter ? ConversationState.Abandoned : ConversationState.Ongoing;
    }

    public static double Minutes(TimeSpan span) => Math.Round(span.TotalMinutes, 1);

    /// <summary>
    /// Protects a CSV cell against spreadsheet formula injection: a cell that starts with = + - @ (or a tab / carriage return) gets a
    /// leading apostrophe, then normal CSV quoting. Labels are the only free text in the export.
    /// </summary>
    public static string CsvCell(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            text = "'" + text;
        }

        return text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r')
            ? "\"" + text.Replace("\"", "\"\"") + "\""
            : text;
    }

    public static string CsvRow(IEnumerable<string?> cells) => string.Join(',', cells.Select(CsvCell));

    public static string Number(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    public static string Time(DateTimeOffset? value) => value?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>The start of the period (day, or ISO week starting Monday) a time belongs to, in UTC.</summary>
    public static DateTimeOffset PeriodStart(DateTimeOffset time, bool weekly)
    {
        var day = new DateTimeOffset(time.UtcDateTime.Date, TimeSpan.Zero);
        if (!weekly)
        {
            return day;
        }

        var offset = ((int)day.DayOfWeek + 6) % 7; // Monday = 0
        return day.AddDays(-offset);
    }

    public static string CsvHeader { get; } = CsvRow(
    [
        "label", "status", "created_at", "first_login_at", "last_login_at", "login_count", "conversations", "ongoing", "completed", "abandoned",
        "user_messages", "assistant_messages", "last_activity_at", "estimated_active_minutes",
    ]);

    public static string CsvLine(ParticipantStats s) => CsvRow(
    [
        s.Label, s.Status.ToString(), Time(s.CreatedAt), Time(s.FirstLoginAt), Time(s.LastLoginAt),
        s.LoginCount.ToString(CultureInfo.InvariantCulture), s.ConversationCount.ToString(CultureInfo.InvariantCulture),
        s.OngoingConversations.ToString(CultureInfo.InvariantCulture), s.CompletedConversations.ToString(CultureInfo.InvariantCulture),
        s.AbandonedConversations.ToString(CultureInfo.InvariantCulture), s.UserMessages.ToString(CultureInfo.InvariantCulture),
        s.AssistantMessages.ToString(CultureInfo.InvariantCulture), Time(s.LastActivityAt), Number(s.EstimatedActiveMinutes),
    ]);

    public static string Csv(IEnumerable<ParticipantStats> rows)
    {
        var sb = new StringBuilder(CsvHeader).Append("\r\n");
        foreach (var row in rows)
        {
            sb.Append(CsvLine(row)).Append("\r\n");
        }

        return sb.ToString();
    }
}
