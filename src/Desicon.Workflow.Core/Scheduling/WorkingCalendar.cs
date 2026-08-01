using Desicon.Workflow.Core.Engine;

namespace Desicon.Workflow.Core.Scheduling;

/// <summary>
/// Working-hours arithmetic. Used for SLA deadlines and for the cash advance
/// retirement clock.
///
/// Calendar hours and working hours are not interchangeable and the difference
/// is large. On a 9-hour working day, 72 working hours is eight working days --
/// well over a calendar week once a weekend falls inside it. Every overdue
/// figure on the dashboard depends on which of the two is meant, so the choice
/// is explicit configuration rather than a default buried in code.
/// </summary>
public interface IWorkingCalendar
{
    string Key { get; }

    bool IsWorkingDay(DateOnly day);

    /// <summary>Advances by the given number of working hours, skipping
    /// non-working hours, weekends and holidays.</summary>
    DateTimeOffset AddWorkingHours(DateTimeOffset from, decimal hours);

    /// <summary>Working hours elapsed between two instants. Used for turnaround
    /// reporting, so an approver is not charged for the weekend.</summary>
    decimal WorkingHoursBetween(DateTimeOffset from, DateTimeOffset until);
}

public sealed class WorkingCalendarOptions
{
    public string Key { get; init; } = "NG_STANDARD";

    /// <summary>Nigeria is UTC+1 year round, with no daylight saving.</summary>
    public TimeSpan UtcOffset { get; init; } = TimeSpan.FromHours(1);

    public TimeOnly DayStart { get; init; } = new(8, 0);

    public TimeOnly DayEnd { get; init; } = new(17, 0);

    public IReadOnlySet<DayOfWeek> WorkingDays { get; init; } = new HashSet<DayOfWeek>
    {
        DayOfWeek.Monday,
        DayOfWeek.Tuesday,
        DayOfWeek.Wednesday,
        DayOfWeek.Thursday,
        DayOfWeek.Friday
    };

    /// <summary>
    /// Declared public holidays.
    ///
    /// Nigerian holidays cannot be fully computed. Eid al-Fitr, Eid al-Adha and
    /// Maulud follow the lunar calendar and are declared by the Federal
    /// Government each year, sometimes only days ahead, and the declaration
    /// often adds a second day. So this is a maintained table, not an algorithm,
    /// and it needs an owner and an annual update task -- an SLA that silently
    /// counts Eid as a working day will produce false breach alerts.
    /// </summary>
    public IReadOnlySet<DateOnly> Holidays { get; init; } = new HashSet<DateOnly>();

    public decimal HoursPerDay =>
        (decimal)(DayEnd.ToTimeSpan() - DayStart.ToTimeSpan()).TotalHours;
}

public sealed class WorkingCalendar : IWorkingCalendar
{
    private readonly WorkingCalendarOptions _options;

    public WorkingCalendar(WorkingCalendarOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (_options.DayEnd <= _options.DayStart)
        {
            throw new ArgumentException(
                "Working day end must be after its start.", nameof(options));
        }

        if (_options.WorkingDays.Count == 0)
        {
            throw new ArgumentException(
                "A calendar with no working days can never advance.", nameof(options));
        }
    }

    public string Key => _options.Key;

    public bool IsWorkingDay(DateOnly day) =>
        _options.WorkingDays.Contains(day.DayOfWeek) && !_options.Holidays.Contains(day);

    public DateTimeOffset AddWorkingHours(DateTimeOffset from, decimal hours)
    {
        if (hours < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(hours), "Use a positive number of hours.");
        }

        var cursor = ToLocal(from);
        var remaining = hours;

        // Land on a working moment first. Anything raised at 18:00 on a Friday
        // starts its clock at 08:00 on Monday, not immediately.
        cursor = AdvanceToWorkingMoment(cursor);

        if (remaining == 0)
        {
            return ToOffset(cursor);
        }

        while (remaining > 0)
        {
            var dayEnd = OnDate(DateOnly.FromDateTime(cursor.DateTime), _options.DayEnd);
            var availableToday = (decimal)(dayEnd - cursor).TotalHours;

            if (availableToday >= remaining)
            {
                return ToOffset(cursor.AddHours((double)remaining));
            }

            remaining -= availableToday;
            cursor = NextWorkingDayStart(DateOnly.FromDateTime(cursor.DateTime));
        }

        return ToOffset(cursor);
    }

    public decimal WorkingHoursBetween(DateTimeOffset from, DateTimeOffset until)
    {
        if (until <= from)
        {
            return 0m;
        }

        var start = AdvanceToWorkingMoment(ToLocal(from));
        var end = ToLocal(until);

        if (end <= start)
        {
            return 0m;
        }

        var total = 0m;
        var cursor = start;

        while (cursor < end)
        {
            var date = DateOnly.FromDateTime(cursor.DateTime);
            var dayEnd = OnDate(date, _options.DayEnd);
            var segmentEnd = end < dayEnd ? end : dayEnd;

            if (segmentEnd > cursor)
            {
                total += (decimal)(segmentEnd - cursor).TotalHours;
            }

            cursor = NextWorkingDayStart(date);

            if (cursor >= end)
            {
                break;
            }
        }

        return Math.Round(total, 4, MidpointRounding.AwayFromZero);
    }

    private DateTimeOffset AdvanceToWorkingMoment(DateTimeOffset cursor)
    {
        var date = DateOnly.FromDateTime(cursor.DateTime);

        while (!IsWorkingDay(date))
        {
            date = date.AddDays(1);
            cursor = OnDate(date, _options.DayStart);
        }

        var dayStart = OnDate(date, _options.DayStart);
        var dayEnd = OnDate(date, _options.DayEnd);

        if (cursor < dayStart)
        {
            return dayStart;
        }

        if (cursor >= dayEnd)
        {
            return NextWorkingDayStart(date);
        }

        return cursor;
    }

    private DateTimeOffset NextWorkingDayStart(DateOnly date)
    {
        var next = date.AddDays(1);

        // A long public holiday run plus a weekend can span a week; the bound
        // stops a misconfigured calendar spinning forever.
        for (var guard = 0; guard < 400; guard++)
        {
            if (IsWorkingDay(next))
            {
                return OnDate(next, _options.DayStart);
            }

            next = next.AddDays(1);
        }

        throw new InvalidOperationException(
            $"Calendar '{Key}' has no working day within 400 days of {date:O}. " +
            "Check the holiday table.");
    }

    private DateTimeOffset OnDate(DateOnly date, TimeOnly time) =>
        new(date.Year, date.Month, date.Day, time.Hour, time.Minute, 0, _options.UtcOffset);

    private DateTimeOffset ToLocal(DateTimeOffset value) =>
        value.ToOffset(_options.UtcOffset);

    private static DateTimeOffset ToOffset(DateTimeOffset value) => value;
}

/// <summary>
/// Default clock backed by a working calendar registry.
/// </summary>
public sealed class WorkflowClock : IWorkflowClock
{
    private readonly Dictionary<string, IWorkingCalendar> _calendars;
    private readonly TimeProvider _timeProvider;

    public WorkflowClock(
        IEnumerable<IWorkingCalendar> calendars, TimeProvider? timeProvider = null)
    {
        _calendars = calendars.ToDictionary(c => c.Key, StringComparer.Ordinal);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public DateTimeOffset UtcNow => _timeProvider.GetUtcNow();

    public DateTimeOffset AddWorkingHours(DateTimeOffset from, int hours, string calendarKey)
    {
        if (!_calendars.TryGetValue(calendarKey, out var calendar))
        {
            throw new InvalidOperationException(
                $"Working calendar '{calendarKey}' is not registered.");
        }

        return calendar.AddWorkingHours(from, hours);
    }

    public IWorkingCalendar Calendar(string key) =>
        _calendars.TryGetValue(key, out var calendar)
            ? calendar
            : throw new InvalidOperationException(
                $"Working calendar '{key}' is not registered.");
}

/// <summary>
/// Fixed-date Nigerian public holidays. Lunar holidays are not here by design --
/// see <see cref="WorkingCalendarOptions.Holidays"/>.
/// </summary>
public static class NigerianHolidays
{
    public static IReadOnlySet<DateOnly> FixedDatesFor(int year)
    {
        var dates = new HashSet<DateOnly>
        {
            new(year, 1, 1),    // New Year's Day
            new(year, 5, 1),    // Workers' Day
            new(year, 6, 12),   // Democracy Day
            new(year, 10, 1),   // Independence Day
            new(year, 12, 25),  // Christmas Day
            new(year, 12, 26)   // Boxing Day
        };

        var easter = EasterSunday(year);
        dates.Add(easter.AddDays(-2)); // Good Friday
        dates.Add(easter.AddDays(1));  // Easter Monday

        return dates;
    }

    /// <summary>Anonymous Gregorian computus.</summary>
    private static DateOnly EasterSunday(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = ((19 * a) + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + (2 * e) + (2 * i) - h - k) % 7;
        var m = (a + (11 * h) + (22 * l)) / 451;
        var month = (h + l - (7 * m) + 114) / 31;
        var day = ((h + l - (7 * m) + 114) % 31) + 1;

        return new DateOnly(year, month, day);
    }
}
