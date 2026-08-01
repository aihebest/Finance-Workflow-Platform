using Desicon.Workflow.Core.Scheduling;
using Desicon.Workflow.Domain.Common;
using Desicon.Workflow.Domain.Requests;
using FluentAssertions;
using Xunit;

namespace Desicon.Workflow.UnitTests.Engine;

/// <summary>
/// The retirement clock runs on WORKING hours from cash release, per Finance
/// direction of 31 July 2026.
///
/// These tests exist mainly to make the consequence visible. On a 9-hour
/// working day, 72 working hours is eight working days -- a Friday-afternoon
/// release is not overdue until nearly two calendar weeks later. If the
/// dashboard ever looks implausibly clean, this is the first place to look.
/// </summary>
public sealed class WorkingCalendarTests
{
    // Standard Desicon week: Mon-Fri, 08:00-17:00 WAT (UTC+1), 9 hours a day.
    private static WorkingCalendar Standard(params DateOnly[] holidays) =>
        new(new WorkingCalendarOptions
        {
            Key = "NG_STANDARD",
            UtcOffset = TimeSpan.FromHours(1),
            DayStart = new TimeOnly(8, 0),
            DayEnd = new TimeOnly(17, 0),
            Holidays = holidays.ToHashSet()
        });

    private static DateTimeOffset Wat(int y, int m, int d, int h, int min = 0) =>
        new(y, m, d, h, min, 0, TimeSpan.FromHours(1));

    [Fact]
    public void Nine_hour_day_is_consumed_exactly()
    {
        var calendar = Standard();

        calendar.AddWorkingHours(Wat(2026, 8, 3, 8, 0), 9)
            .Should().Be(Wat(2026, 8, 3, 17, 0));
    }

    [Fact]
    public void In_station_window_is_two_and_two_thirds_working_days()
    {
        // Monday 08:00 + 24 working hours -> Wednesday 14:00.
        Standard().AddWorkingHours(Wat(2026, 8, 3, 8, 0), 24)
            .Should().Be(Wat(2026, 8, 5, 14, 0));
    }

    [Fact]
    public void Out_of_station_window_is_eight_working_days()
    {
        // Monday 08:00 + 72 working hours -> the following Wednesday 17:00.
        // Nine calendar days, not three.
        Standard().AddWorkingHours(Wat(2026, 8, 3, 8, 0), 72)
            .Should().Be(Wat(2026, 8, 12, 17, 0));
    }

    [Fact]
    public void Friday_afternoon_release_pushes_the_out_of_station_deadline_twelve_days()
    {
        // The case worth showing Finance before this goes live.
        var due = Standard().AddWorkingHours(Wat(2026, 8, 7, 16, 0), 72);

        due.Should().Be(Wat(2026, 8, 19, 16, 0));
        (due - Wat(2026, 8, 7, 16, 0)).TotalDays.Should().Be(12);
    }

    [Fact]
    public void Clock_does_not_start_until_the_next_working_moment()
    {
        // Released at 18:30 on a Wednesday: the clock starts Thursday 08:00.
        Standard().AddWorkingHours(Wat(2026, 8, 5, 18, 30), 9)
            .Should().Be(Wat(2026, 8, 6, 17, 0));
    }

    [Fact]
    public void Weekend_release_starts_on_monday()
    {
        Standard().AddWorkingHours(Wat(2026, 8, 8, 10, 0), 24)
            .Should().Be(Wat(2026, 8, 12, 14, 0));
    }

    [Fact]
    public void Holidays_are_skipped()
    {
        // Independence Day, Thursday 1 October 2026.
        var calendar = Standard(new DateOnly(2026, 10, 1));

        calendar.AddWorkingHours(Wat(2026, 9, 30, 8, 0), 18)
            .Should().Be(Wat(2026, 10, 2, 17, 0));
    }

    [Fact]
    public void Fixed_holiday_table_includes_the_national_days()
    {
        var holidays = NigerianHolidays.FixedDatesFor(2026);

        holidays.Should().Contain(new DateOnly(2026, 10, 1));  // Independence
        holidays.Should().Contain(new DateOnly(2026, 6, 12));   // Democracy Day
        holidays.Should().Contain(new DateOnly(2026, 5, 1));    // Workers' Day
        holidays.Should().Contain(new DateOnly(2026, 4, 3));    // Good Friday 2026
        holidays.Should().Contain(new DateOnly(2026, 4, 6));    // Easter Monday 2026
    }

    [Fact]
    public void Turnaround_measurement_does_not_charge_an_approver_for_the_weekend()
    {
        // Assigned Friday 16:00, actioned Monday 09:00. One working hour on
        // Friday plus one on Monday -- not 65.
        Standard().WorkingHoursBetween(Wat(2026, 8, 7, 16, 0), Wat(2026, 8, 10, 9, 0))
            .Should().Be(2m);
    }

    [Fact]
    public void A_calendar_with_no_working_days_is_rejected_at_construction()
    {
        var act = () => new WorkingCalendar(new WorkingCalendarOptions
        {
            WorkingDays = new HashSet<DayOfWeek>()
        });

        act.Should().Throw<ArgumentException>();
    }
}

public sealed class RetirementClockTests
{
    private static readonly WorkingCalendar Calendar = new(new WorkingCalendarOptions
    {
        UtcOffset = TimeSpan.FromHours(1)
    });

    private static CashAdvanceRequest Advance(StationScope scope)
    {
        var advance = new CashAdvanceRequest { StationScope = scope };
        advance.Lines.Add(new AdvanceLine { Amount = 14_000m, AmountNgn = 14_000m });
        advance.RecalculateTotals();
        return advance;
    }

    [Fact]
    public void Clock_starts_at_cash_release_not_at_acknowledgement()
    {
        var released = new DateTimeOffset(2026, 8, 3, 9, 0, 0, TimeSpan.FromHours(1));
        var advance = Advance(StationScope.InStation);

        advance.ReleaseCash(released, Calendar);

        advance.CashReleasedAt.Should().Be(released);
        advance.RetirementDueDate.Should().NotBeNull();

        // A later acknowledgement must not move the deadline.
        var dueBefore = advance.RetirementDueDate;
        advance.Acknowledge(Guid.NewGuid(), released.AddDays(2));

        advance.RetirementDueDate.Should().Be(dueBefore);
    }

    [Fact]
    public void In_station_advance_released_monday_morning_is_due_wednesday_afternoon()
    {
        var advance = Advance(StationScope.InStation);

        advance.ReleaseCash(
            new DateTimeOffset(2026, 8, 3, 8, 0, 0, TimeSpan.FromHours(1)), Calendar);

        advance.RetirementDueDate.Should()
            .Be(new DateTimeOffset(2026, 8, 5, 14, 0, 0, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Out_of_station_advance_gets_eight_working_days()
    {
        var advance = Advance(StationScope.OutOfStation);

        advance.ReleaseCash(
            new DateTimeOffset(2026, 8, 3, 8, 0, 0, TimeSpan.FromHours(1)), Calendar);

        advance.RetirementDueDate.Should()
            .Be(new DateTimeOffset(2026, 8, 12, 17, 0, 0, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Overdue_only_once_the_working_hours_deadline_passes()
    {
        var released = new DateTimeOffset(2026, 8, 3, 8, 0, 0, TimeSpan.FromHours(1));
        var advance = Advance(StationScope.InStation);
        advance.ReleaseCash(released, Calendar);

        // Two calendar days later is still inside the window.
        advance.RecalculateRetirementStatus(released.AddDays(2));
        advance.RetirementStatus.Should().Be(RetirementStatus.Due);

        advance.RecalculateRetirementStatus(released.AddDays(3));
        advance.RetirementStatus.Should().Be(RetirementStatus.Overdue);
    }

    [Fact]
    public void Policy_override_allows_the_window_to_change_without_a_deployment()
    {
        var advance = Advance(StationScope.OutOfStation);

        advance.ReleaseCash(
            new DateTimeOffset(2026, 8, 3, 8, 0, 0, TimeSpan.FromHours(1)),
            Calendar,
            overrideWindowHours: 27m); // three 9-hour days

        advance.RetirementDueDate.Should()
            .Be(new DateTimeOffset(2026, 8, 5, 17, 0, 0, TimeSpan.FromHours(1)));
    }
}
