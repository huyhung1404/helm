using System.Globalization;
using System.Text.RegularExpressions;
using Helm.Core.Capture;
using Helm.Core.Settings;
using Helm.Core.Text;

namespace Helm.Modules.Tracker;

/// <summary>A task as typed in Quick Capture, e.g. "buy milk tomorrow 9h #Home !".</summary>
/// <param name="List">The text after '#', naming a to-do list; null for the default list.</param>
/// <param name="Day">The due day; null when no day or time was typed.</param>
/// <param name="Time">The due time of day; null for a whole day or none.</param>
public sealed record TaskCapture(string Title, TrackerPriority Priority, string? List, DateOnly? Day, TimeSpan? Time);

/// <summary>A debt as typed in Quick Capture, e.g. "Nam 200k lunch".</summary>
public sealed record DebtCapture(string Person, decimal Amount, DebtEntryKind Kind, string Note);

/// <summary>
/// Reads the short forms Quick Capture accepts for Tracker, in English and Vietnamese. Days and times are read only at
/// the end of the text, so words like "mai" inside a title stay in it; a capitalised "Mai" is always a name.
/// </summary>
public static partial class TrackerCaptureParser
{
    private static readonly string[] Weekdays = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];

    /// <summary>Vietnamese weekday words after "thứ": hai (Mon) … bảy (Sat), folded.</summary>
    private static readonly Dictionary<string, DayOfWeek> VietnameseWeekdays = new(StringComparer.Ordinal)
    {
        ["2"] = DayOfWeek.Monday, ["hai"] = DayOfWeek.Monday,
        ["3"] = DayOfWeek.Tuesday, ["ba"] = DayOfWeek.Tuesday,
        ["4"] = DayOfWeek.Wednesday, ["tu"] = DayOfWeek.Wednesday,
        ["5"] = DayOfWeek.Thursday, ["nam"] = DayOfWeek.Thursday,
        ["6"] = DayOfWeek.Friday, ["sau"] = DayOfWeek.Friday,
        ["7"] = DayOfWeek.Saturday, ["bay"] = DayOfWeek.Saturday,
    };

    /// <param name="now">Local now: a time that has already passed today means tomorrow.</param>
    public static TaskCapture ParseTask(string text, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        var priority = TrackerPriority.Normal;
        string? list = null;
        for (var i = tokens.Count - 1; i >= 0; i--)
        {
            var token = tokens[i];
            if (token is "!!" or "!!!")
            {
                priority = TrackerPriority.Urgent;
                tokens.RemoveAt(i);
            }
            else if (token == "!")
            {
                if (priority != TrackerPriority.Urgent) priority = TrackerPriority.High;
                tokens.RemoveAt(i);
            }
            else if (token.Length > 1 && token[0] == '#')
            {
                list ??= token[1..];
                tokens.RemoveAt(i);
            }
        }

        DateOnly? day = null;
        TimeSpan? time = null;
        while (tokens.Count > 0)
        {
            var last = tokens[^1];
            var folded = TextSearch.Fold(last);
            var before = tokens.Count > 1 ? TextSearch.Fold(tokens[^2]) : null;

            // "3h chiều", "8h tối": the part of the day after a time.
            if (time is null && folded is "sang" or "trua" or "chieu" or "toi" or "dem" && tokens.Count > 1 && TryTime(tokens[^2], out var t0))
            {
                time = folded is "chieu" or "toi" or "dem" && t0.Hours < 12 ? t0.Add(TimeSpan.FromHours(12)) : t0;
                tokens.RemoveRange(tokens.Count - 2, 2);
                continue;
            }
            if (time is null && TryTime(last, out var t1))
            {
                time = t1;
                tokens.RemoveAt(tokens.Count - 1);
                continue;
            }
            if (day is null && before is not null && TryTwoWordDay(before, folded, today) is { } d2)
            {
                day = d2;
                tokens.RemoveRange(tokens.Count - 2, 2);
                continue;
            }
            if (day is null && TryOneWordDay(last, folded, today) is { } d1)
            {
                day = d1;
                tokens.RemoveAt(tokens.Count - 1);
                continue;
            }
            break;
        }

        // A time alone is today, or tomorrow once it has passed.
        if (time is { } at && day is null) day = now.TimeOfDay < at ? today : today.AddDays(1);
        return new TaskCapture(string.Join(' ', tokens), priority, list, day, time);
    }

    /// <summary>
    /// "Nam 200k [note]": Nam owes me. "Nam -200k" or "nợ Nam 200k": I owe Nam. "Nam trả 50k": a repayment.
    /// "Nam nợ 200k": Nam owes me. Null when there is no person or no amount.
    /// </summary>
    public static DebtCapture? ParseDebt(string text)
    {
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        for (var i = 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var sign = token[0] is '-' or '+' ? token[0] : ' ';
            var number = sign == ' ' ? token : token[1..];
            if (!number.Any(char.IsDigit) || !TrackerFormat.TryParseAmount(number, out var amount) || amount <= 0) continue;

            var person = tokens.Take(i).ToList();
            var kind = sign == '-' ? DebtEntryKind.IOwe : DebtEntryKind.OwesMe;
            var lastWord = TextSearch.Fold(person[^1]);
            if (person.Count > 1 && lastWord is "tra" or "paid" or "repaid" or "repay")
            {
                kind = DebtEntryKind.Repayment;
                person.RemoveAt(person.Count - 1);
            }
            else if (person.Count > 1 && lastWord == "no")
            {
                kind = DebtEntryKind.OwesMe;
                person.RemoveAt(person.Count - 1);
            }
            else if (person.Count > 1 && TextSearch.Fold(person[0]) == "no" && sign != '+')
            {
                kind = DebtEntryKind.IOwe;
                person.RemoveAt(0);
            }
            var name = string.Join(' ', person).Trim();
            if (name.Length == 0) return null;
            return new DebtCapture(name, amount, kind, string.Join(' ', tokens.Skip(i + 1)));
        }
        return null;
    }

    /// <summary>"9h", "9h30", "14:30", "9am", "9:30pm", "21h".</summary>
    internal static bool TryTime(string token, out TimeSpan time)
    {
        time = default;
        var m = TimePattern().Match(token.ToLowerInvariant());
        if (!m.Success || (!m.Groups["sep"].Success && !m.Groups["ampm"].Success)) return false;
        var hours = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minutes = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        if (m.Groups["ampm"].Success)
        {
            if (hours is < 1 or > 12) return false;
            hours = hours % 12 + (m.Groups["ampm"].Value == "pm" ? 12 : 0);
        }
        if (hours > 23 || minutes > 59) return false;
        time = new TimeSpan(hours, minutes, 0);
        return true;
    }

    private static DateOnly? TryOneWordDay(string original, string folded, DateOnly today)
    {
        // Single words are matched with their accents: without them Vietnamese words collide ("mái" and "mai",
        // "một" and "mốt", "món" and "mon"). A capitalised word is a name ("Mai").
        var lower = original.ToLowerInvariant();
        if (original == lower)
        {
            switch (lower)
            {
                case "today" or "hnay":
                    return today;
                case "tomorrow" or "tmr" or "tmrw" or "mai":
                    return today.AddDays(1);
                case "mốt":
                    return today.AddDays(2);
                case "cn":
                    return Next(today, DayOfWeek.Sunday);
            }
        }
        if (lower.Length == 2 && lower[0] == 't' && char.IsDigit(lower[1]) && VietnameseWeekdays.TryGetValue(lower[1..], out var viet))
            return Next(today, viet);
        if (lower == folded)
        {
            // English weekdays, whole or shortened to 3-4 letters ("mon", "thur").
            for (var i = 0; i < Weekdays.Length; i++)
            {
                if (folded == Weekdays[i] || (folded.Length is >= 3 and <= 4 && Weekdays[i].StartsWith(folded, StringComparison.Ordinal)))
                    return Next(today, (DayOfWeek)i);
            }
        }
        var date = DatePattern().Match(folded);
        if (date.Success)
        {
            var d = int.Parse(date.Groups["d"].Value, CultureInfo.InvariantCulture);
            var mo = int.Parse(date.Groups["mo"].Value, CultureInfo.InvariantCulture);
            var year = date.Groups["y"].Success ? int.Parse(date.Groups["y"].Value, CultureInfo.InvariantCulture) : today.Year;
            if (year < 100) year += 2000;
            if (mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(year, mo)) return null;
            var parsed = new DateOnly(year, mo, d);
            // "3/1" in December is next year's.
            if (!date.Groups["y"].Success && parsed < today && mo < today.Month) parsed = parsed.AddYears(1);
            return parsed;
        }
        return null;
    }

    private static DateOnly? TryTwoWordDay(string first, string second, DateOnly today) => (first, second) switch
    {
        ("hom", "nay") => today,
        ("ngay", "mai") => today.AddDays(1),
        ("ngay", "kia") => today.AddDays(2),
        ("chu", "nhat") => Next(today, DayOfWeek.Sunday),
        ("thu", _) when VietnameseWeekdays.TryGetValue(second, out var day) => Next(today, day),
        _ => null,
    };

    /// <summary>The next <paramref name="day"/> after today (a week ahead when today is that day).</summary>
    private static DateOnly Next(DateOnly today, DayOfWeek day)
    {
        var ahead = ((int)day - (int)today.DayOfWeek + 7) % 7;
        return today.AddDays(ahead == 0 ? 7 : ahead);
    }

    [GeneratedRegex(@"^(?<h>\d{1,2})(?:(?<sep>[h:])(?<m>\d{2})?)?(?<ampm>am|pm)?$", RegexOptions.CultureInvariant)]
    private static partial Regex TimePattern();

    [GeneratedRegex(@"^(?<d>\d{1,2})[/.-](?<mo>\d{1,2})(?:[/.-](?<y>\d{2}|\d{4}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex DatePattern();
}

/// <summary>Quick Capture into a to-do list: "buy milk tomorrow 9h #Home !".</summary>
public sealed class TaskCaptureTarget(TrackerStore store, ISettingsStoreFactory settings) : ICaptureTarget
{
    public const string TargetId = "task";

    public string Id => TargetId;
    public string ModuleId => TrackerIds.ModuleId;
    public string Name => "Task";
    public string Prefix => "t";
    public string Example => "buy milk tomorrow 9h #Home !  (! high, !! urgent, #list)";
    public int Order => 10;

    public CapturePreview Preview(string text)
    {
        var (task, workspace, problem) = Read(text);
        if (problem is not null) return new CapturePreview(false, problem);
        var parts = new List<string> { $"Task in {workspace!.Value.Name}" };
        if (Due(task!) is { } due) parts.Add(TrackerFormat.Due(new TrackerItem { DueAt = due.At, DueDate = due.Day }, store.Now));
        if (task!.Priority != TrackerPriority.Normal) parts.Add(TrackerFormat.Priority(task.Priority));
        return new CapturePreview(true, string.Join(" · ", parts));
    }

    public CaptureResult Capture(string text)
    {
        var (task, workspace, problem) = Read(text);
        if (problem is not null) return new CaptureResult(false, problem);
        try
        {
            var due = Due(task!);
            store.AddItem(workspace!.Value.Id, new TrackerItemDraft(task!.Title, task.Priority, due?.Day, DueAt: due?.At));
            return new CaptureResult(true, $"Added to {workspace.Value.Name}: “{task.Title}”.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return new CaptureResult(false, ex.Message);
        }
    }

    private (TaskCapture? Task, (string Id, string Name)? Workspace, string? Problem) Read(string text)
    {
        var task = TrackerCaptureParser.ParseTask(text, TimeZoneInfo.ConvertTime(store.Now, TimeZoneInfo.Local).DateTime);
        var lists = store.Workspaces().Where(w => w.Value.Kind == WorkspaceKind.Tasks).ToList();
        if (lists.Count == 0) return (task, null, "Create a to-do list in Tracker first.");
        SyncedWorkspace? chosen;
        if (task.List is { } wanted)
        {
            var terms = TextSearch.Terms(wanted);
            chosen = lists.Select(w => (w, score: TextSearch.Score(terms, w.Value.Name))).Where(x => x.score > 0)
                .OrderByDescending(x => x.score).Select(x => (SyncedWorkspace?)new SyncedWorkspace(x.w.Id, x.w.Value.Name)).FirstOrDefault();
            if (chosen is null) return (task, null, $"No to-do list is called “{wanted}”.");
        }
        else
        {
            // The list open in Tracker, else the first one.
            var selected = settings.Get<TrackerSettings>(TrackerIds.ModuleId).Current.SelectedWorkspaceId;
            var w = lists.FirstOrDefault(l => l.Id == selected) ?? lists[0];
            chosen = new SyncedWorkspace(w.Id, w.Value.Name);
        }
        if (task.Title.Length == 0) return (task, null, "Type what needs doing.");
        return (task, (chosen.Value.Id, chosen.Value.Name), null);
    }

    private static (DateOnly Day, DateTimeOffset? At)? Due(TaskCapture task)
    {
        if (task.Day is not { } day) return null;
        if (task.Time is not { } time) return (day, null);
        var local = day.ToDateTime(TimeOnly.FromTimeSpan(time));
        return (day, new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)));
    }

    private readonly record struct SyncedWorkspace(string Id, string Name);
}

/// <summary>Quick Capture into the debt book: "Nam 200k lunch", "Nam -200k", "Nam trả 50k".</summary>
public sealed class DebtCaptureTarget(TrackerStore store) : ICaptureTarget
{
    public const string TargetId = "debt";

    public string Id => TargetId;
    public string ModuleId => TrackerIds.ModuleId;
    public string Name => "Debt";
    public string Prefix => "d";
    public string Example => "Nam 200k lunch · Nam -200k (you owe) · Nam trả 50k (paid back)";
    public int Order => 20;

    public CapturePreview Preview(string text)
    {
        var (debt, book, problem) = Read(text);
        if (problem is not null) return new CapturePreview(false, problem);
        var money = TrackerFormat.Money(debt!.Amount, book!.Currency);
        var what = debt.Kind switch
        {
            DebtEntryKind.IOwe => $"You owe {debt.Person} {money}",
            DebtEntryKind.Repayment => store.DebtBalance(book.Id, debt.Person) switch
            {
                > 0 => $"{debt.Person} pays you back {money}",
                < 0 => $"You pay {debt.Person} back {money}",
                _ => null,
            },
            _ => $"{debt.Person} owes you {money}",
        };
        if (what is null) return new CapturePreview(false, $"{debt.Person} has nothing to repay: the balance is 0.");
        return new CapturePreview(true, debt.Note.Length > 0 ? $"{what} · {debt.Note}" : what);
    }

    public CaptureResult Capture(string text)
    {
        var (debt, book, problem) = Read(text);
        if (problem is not null) return new CaptureResult(false, problem);
        try
        {
            store.AddDebt(book!.Id, debt!.Person, debt.Amount, debt.Kind, debt.Note);
            var balance = store.DebtBalance(book.Id, debt.Person);
            return new CaptureResult(true, $"Saved to {book.Name}. {debt.Person}: {TrackerFormat.Balance(balance, book.Currency)}.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return new CaptureResult(false, ex.Message);
        }
    }

    private (DebtCapture? Debt, Book? Book, string? Problem) Read(string text)
    {
        var book = store.Workspaces().FirstOrDefault(w => w.Value.Kind == WorkspaceKind.Debts);
        if (book is null) return (null, null, "Create the debt book in Tracker first.");
        var debt = TrackerCaptureParser.ParseDebt(text);
        if (debt is null) return (null, null, "Type who and how much: Nam 200k (owes you), Nam -200k (you owe), Nam trả 50k (paid back).");
        return (debt, new Book(book.Id, book.Value.Name, book.Value.Currency), null);
    }

    private sealed record Book(string Id, string Name, string Currency);
}
