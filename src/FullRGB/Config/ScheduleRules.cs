using System.Globalization;

namespace FullRGB.Config;

/// <summary>One time-of-day rule: switch to a profile between two wall-clock times.</summary>
public sealed class ScheduleRule
{
    /// <summary>Local start time "hh:mm" (inclusive).</summary>
    public string Start { get; set; } = "00:00";
    /// <summary>Local end time "hh:mm" (exclusive). End &lt; start = the range wraps midnight.</summary>
    public string End { get; set; } = "00:00";
    /// <summary>Profile to activate while the rule matches.</summary>
    public string Profile { get; set; } = "";
    /// <summary>Active weekdays. Empty = every day. Values: Mo Tu We Th Fr Sa Su.</summary>
    public HashSet<DayOfWeek> Days { get; set; } = new();

    public bool IsOvernight => StartMinutes() > EndMinutes();

    public int StartMinutes() => ParseHm(Start);
    public int EndMinutes() => ParseHm(End);

    /// <summary>Does this rule match the given local moment? Pure; unit-tested.</summary>
    public bool Matches(DateTime local)
    {
        if (Days.Count > 0 && !Days.Contains(local.DayOfWeek)) return false;
        int t = local.Hour * 60 + local.Minute;
        int s = StartMinutes(), e = EndMinutes();
        if (IsOvernight) return t >= s || t < e;
        // equal start/end = the whole day (a degenerate "22:00-22:00" should not be dead)
        if (s == e) return true;
        return t >= s && t < e;
    }

    private static int ParseHm(string s)
    {
        if (TryParseHm(s, out int m)) return m;
        return 0;
    }

    public static bool TryParseHm(string? s, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var parts = s.Trim().Split(':');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m)) return false;
        if (h < 0 || h > 23 || m < 0 || m > 59) return false;
        minutes = h * 60 + m;
        return true;
    }

    public string Describe()
    {
        string days = Days.Count == 0 ? "" : string.Join(",",
            Enum.GetValues<DayOfWeek>().Where(Days.Contains).Select(DayCode)) + " ";
        return $"{days}{Start}-{End}={Profile}";
    }

    private static string DayCode(DayOfWeek d) => d switch
    {
        DayOfWeek.Monday => "Mo", DayOfWeek.Tuesday => "Tu", DayOfWeek.Wednesday => "We",
        DayOfWeek.Thursday => "Th", DayOfWeek.Friday => "Fr", DayOfWeek.Saturday => "Sa",
        _ => "Su",
    };

    private static bool TryDayCode(string s, out DayOfWeek d)
    {
        d = DayOfWeek.Sunday;
        switch (s.ToLowerInvariant())
        {
            case "mo": case "monday": d = DayOfWeek.Monday; return true;
            case "tu": case "tuesday": d = DayOfWeek.Tuesday; return true;
            case "we": case "wednesday": d = DayOfWeek.Wednesday; return true;
            case "th": case "thursday": d = DayOfWeek.Thursday; return true;
            case "fr": case "friday": d = DayOfWeek.Friday; return true;
            case "sa": case "saturday": d = DayOfWeek.Saturday; return true;
            case "su": case "sunday": d = DayOfWeek.Sunday; return true;
            default: return false;
        }
    }
}

/// <summary>
/// Parses and evaluates the "time schedule" rules the user types on the Settings page.
/// One rule per line: <c>[Days ]hh:mm-hh:mm=ProfileName</c>. Days may be a range
/// (<c>Mo-Fr</c>) or a list (<c>Sa,Su</c>); blank = every day. <c>#</c> lines are comments.
/// The FIRST matching rule wins, so more specific rules belong on top.
/// </summary>
public static class ScheduleRules
{
    /// <summary>Parses the editor text. Never throws; bad lines are reported, good ones kept.</summary>
    public static (List<ScheduleRule> Rules, List<string> Errors) Parse(string? text)
    {
        var rules = new List<ScheduleRule>();
        var errors = new List<string>();
        foreach (var (raw, idx) in (text ?? "").Split('\n').Select((l, i) => (l, i + 1)))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string daysPart = "", rest = line;
            // "[Mo-Fr |Sa,Su ]hh:mm-hh:mm=Profile" — the day spec ends at the first space.
            int sp = line.IndexOf(' ');
            if (sp > 0)
            {
                string head = line[..sp].Trim();
                if (LooksLikeDays(head)) { daysPart = head; rest = line[(sp + 1)..].Trim(); }
            }
            int eq = rest.IndexOf('=');
            if (eq <= 0)
            {
                errors.Add($"{idx}: {line}");
                continue;
            }
            string range = rest[..eq].Trim();
            string profile = rest[(eq + 1)..].Trim();
            if (profile.Length == 0) { errors.Add($"{idx}: {line}"); continue; }

            var rule = new ScheduleRule { Profile = profile };
            // Day spec: "Mo-Fr" (range) or "Sa,Su" (list).
            if (daysPart.Length > 0)
            {
                var dash = daysPart.Contains('-');
                var parts = daysPart.Split(dash ? '-' : ',');
                bool ok = parts.Length >= (dash ? 2 : 1);
                if (ok && dash && parts.Length == 2
                    && ScheduleRuleDay.TryParse(parts[0], out var d0)
                    && ScheduleRuleDay.TryParse(parts[1], out var d1))
                {
                    foreach (var d in ScheduleRuleDay.Range(d0, d1)) rule.Days.Add(d);
                }
                else if (!dash)
                {
                    foreach (var p in parts)
                    {
                        if (!ScheduleRuleDay.TryParse(p, out var d)) { ok = false; break; }
                        rule.Days.Add(d);
                    }
                }
                else ok = false;
                if (!ok) { errors.Add($"{idx}: {line}"); continue; }
            }

            int dash2 = range.IndexOf('-');
            if (dash2 <= 0
                || !ScheduleRule.TryParseHm(range[..dash2], out int sm)
                || !ScheduleRule.TryParseHm(range[(dash2 + 1)..], out int em))
            {
                errors.Add($"{idx}: {line}");
                continue;
            }
            rule.Start = $"{sm / 60:00}:{sm % 60:00}";
            rule.End = $"{em / 60:00}:{em % 60:00}";
            rules.Add(rule);
        }
        return (rules, errors);
    }

    private static bool LooksLikeDays(string head)
        => head.All(c => char.IsLetter(c) || c == ',' || c == '-');

    /// <summary>Profile for the given local moment, or null (no rule matches). First match wins.</summary>
    public static string? Evaluate(List<ScheduleRule> rules, DateTime local)
    {
        foreach (var r in rules)
            if (r.Matches(local)) return r.Profile;
        return null;
    }

    /// <summary>Serializes rules back to editor text (round-trip through Parse).</summary>
    public static string Serialize(List<ScheduleRule> rules)
        => string.Join("\n", rules.Select(r => r.Describe()));
}

internal static class ScheduleRuleDay
{
    public static bool TryParse(string s, out DayOfWeek d)
    {
        d = DayOfWeek.Sunday;
        switch (s.Trim().ToLowerInvariant())
        {
            case "mo": case "mon": case "monday": d = DayOfWeek.Monday; return true;
            case "tu": case "tue": case "tuesday": d = DayOfWeek.Tuesday; return true;
            case "we": case "wed": case "wednesday": d = DayOfWeek.Wednesday; return true;
            case "th": case "thu": case "thursday": d = DayOfWeek.Thursday; return true;
            case "fr": case "fri": case "friday": d = DayOfWeek.Friday; return true;
            case "sa": case "sat": case "saturday": d = DayOfWeek.Saturday; return true;
            case "su": case "sun": case "sunday": d = DayOfWeek.Sunday; return true;
            default: return false;
        }
    }

    public static IEnumerable<DayOfWeek> Range(DayOfWeek from, DayOfWeek to)
    {
        var list = new List<DayOfWeek> { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                                         DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday,
                                         DayOfWeek.Sunday };
        int i = list.IndexOf(from), j = list.IndexOf(to);
        if (i < 0 || j < 0) yield break;
        while (i != j)
        {
            yield return list[i];
            i = (i + 1) % 7;
        }
        yield return list[j];
    }
}
