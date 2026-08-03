using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Core.Scheduling;
using Desicon.Workflow.Domain.Common;
using Desicon.Workflow.Functions;
using Desicon.Workflow.IntegrationTests.Infrastructure;
using FluentAssertions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Desicon.Workflow.IntegrationTests.CashAdvance;

/// <summary>
/// Step 6's acceptance criterion, and the sharpest consequence of the
/// 31 July decision that the retirement clock counts WORKING hours.
///
/// On a 9-hour working day, 72 working hours is eight working days. An
/// advance released at 16:00 on a Friday has one hour left of that Friday,
/// so the remaining 71 hours run from Monday — landing roughly twelve
/// calendar days after release, not three. If Finance meant "72 hours" on
/// the paper form to mean three calendar days, every overdue figure on the
/// dashboard reads low, and this test is where that disagreement surfaces
/// as a number rather than an assumption.
/// </summary>
public sealed class RetirementSweepTests : IntegrationTestBase
{
    public RetirementSweepTests(WorkflowApiFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Out_of_station_advance_released_friday_1600_is_not_overdue_until_twelve_calendar_days_later()
    {
        var releasedAt = NextFridayAt1600();
        releasedAt.DayOfWeek.Should().Be(DayOfWeek.Friday, "the scenario depends on a Friday release");

        Fixture.TimeProvider.SetUtcNow(releasedAt);

        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "SWEEP-A"));

        var advanceId = await WorkflowSteps.DriveCashAdvanceToOutstandingAsync(
            Fixture, org, "Site visit, out of station", 9_000m,
            "TN-SWEEP-0001", "JV-SWEEP-0001", releasedAt, stationScope: "OutOfStation");

        var dueDate = await WithDbAsync(async db =>
        {
            var advance = await db.CashAdvanceRequests.SingleAsync(a => a.RequestId == advanceId);
            advance.StationScope.Should().Be(StationScope.OutOfStation);
            advance.RetirementStatus.Should().Be(RetirementStatus.NotDue);
            return advance.RetirementDueDate!.Value;
        });

        // The claim under test, stated as a number rather than an assumption.
        //
        // 72 working hours from Friday 16:00: one hour remains that Friday,
        // leaving 71. Seven full nine-hour days (Mon-Fri, then Mon-Tue) take
        // 63, and the last 8 land at 16:00 on the second Wednesday — exactly
        // twelve calendar days after release, holidays permitting.
        (dueDate - releasedAt).TotalDays.Should().BeApproximately(12.0, 0.01,
            "72 working hours from a Friday 16:00 release lands twelve calendar days later");

        // Eleven days after release: still inside the window.
        //
        // Due, not NotDue. The two are easy to misread: NotDue means no
        // retirement due date has been set at all, which is only true before
        // cash release. Once a due date exists, an advance inside its window
        // is Due — retirement is owed, just not yet late — and Overdue only
        // once the date has passed. ReleaseCash leaves the status at NotDue
        // and the first recalculation moves it to Due.
        await RunSweepAtAsync(releasedAt.AddDays(11));
        await AssertStatusAsync(advanceId, RetirementStatus.Due);

        // One hour past the stored due date: overdue.
        await RunSweepAtAsync(dueDate.AddHours(1));
        await AssertStatusAsync(advanceId, RetirementStatus.Overdue);
    }

    /// <summary>
    /// The sweep is idempotent. It runs daily and an advance stays overdue
    /// until it is retired, so a second pass must not append a second
    /// RETIREMENT_OVERDUE event to the hash chain — an audit trail that
    /// accumulates one entry per day per overdue advance is unreadable
    /// precisely when it matters.
    /// </summary>
    [Fact]
    public async Task Sweeping_twice_records_the_overdue_finding_once()
    {
        var releasedAt = NextFridayAt1600();
        Fixture.TimeProvider.SetUtcNow(releasedAt);

        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "SWEEP-B"));

        var advanceId = await WorkflowSteps.DriveCashAdvanceToOutstandingAsync(
            Fixture, org, "Site visit, out of station", 4_000m,
            "TN-SWEEP-0002", "JV-SWEEP-0002", releasedAt, stationScope: "OutOfStation");

        var dueDate = await WithDbAsync(async db =>
            (await db.CashAdvanceRequests.SingleAsync(a => a.RequestId == advanceId)).RetirementDueDate!.Value);

        await RunSweepAtAsync(dueDate.AddHours(1));
        await RunSweepAtAsync(dueDate.AddDays(1));

        var overdueEvents = await WithDbAsync(async db => await db.AuditEvents
            .Where(e => e.RequestId == advanceId && e.EventType == "RETIREMENT_OVERDUE")
            .CountAsync());

        overdueEvents.Should().Be(1, "the status only crosses into Overdue once");
    }

    /// <summary>
    /// An advance still inside its window produces no audit noise at all.
    /// Worth asserting explicitly: a sweep that writes an event per evaluation
    /// would bury the findings that carry consequence.
    /// </summary>
    [Fact]
    public async Task Sweeping_an_advance_inside_its_window_writes_no_overdue_event()
    {
        var releasedAt = NextFridayAt1600();
        Fixture.TimeProvider.SetUtcNow(releasedAt);

        var org = await WithDbAsync(db => WorkflowSteps.CreateOrgChartAsync(db, "SWEEP-C"));

        var advanceId = await WorkflowSteps.DriveCashAdvanceToOutstandingAsync(
            Fixture, org, "Local errand", 1_000m,
            "TN-SWEEP-0003", "JV-SWEEP-0003", releasedAt, stationScope: "OutOfStation");

        await RunSweepAtAsync(releasedAt.AddDays(1));

        var overdueEvents = await WithDbAsync(async db => await db.AuditEvents
            .Where(e => e.RequestId == advanceId && e.EventType == "RETIREMENT_OVERDUE")
            .CountAsync());

        overdueEvents.Should().Be(0);

        // Due, not Overdue: the sweep moved it off NotDue because a due date
        // exists, but wrote nothing to the audit chain because the crossing
        // that matters has not happened.
        await AssertStatusAsync(advanceId, RetirementStatus.Due);
    }

    /// <summary>
    /// The next Friday at 16:00 West Africa Time, at or after the shared
    /// fixture's current instant, whose following fortnight contains no
    /// public holiday.
    ///
    /// Not a hardcoded date. FakeTimeProvider refuses to move backwards
    /// ("Cannot go back in time"), and the fixture is shared across the whole
    /// assembly, so by the time these tests run the clock sits wherever
    /// earlier tests left it. Anchoring to a literal date passes in
    /// isolation and fails in a full run — a test that depends on execution
    /// order without saying so.
    ///
    /// The holiday-free requirement is not fussiness. A public holiday inside
    /// the window consumes a working day and pushes the due date out by one
    /// more calendar day: the first draft of this test measured 13.0 days
    /// rather than 12.0 for exactly that reason. Both figures are correct
    /// behaviour; only one of them isolates the working-hours arithmetic from
    /// the holiday table, and it is the arithmetic under test here. That
    /// holidays move the deadline is itself worth knowing, and is why
    /// WorkingCalendarOptions.Holidays needs an owner.
    ///
    /// 16:00 matters: it leaves one working hour in the day, which is what
    /// pushes the remaining 71 hours past the weekend.
    /// </summary>
    private DateTimeOffset NextFridayAt1600()
    {
        var wat = TimeSpan.FromHours(1);
        var now = Fixture.TimeProvider.GetUtcNow().ToOffset(wat);

        using var scope = Fixture.CreateScope();
        var calendar = scope.ServiceProvider.GetRequiredService<IWorkingCalendar>();

        var candidate = new DateTimeOffset(now.Year, now.Month, now.Day, 16, 00, 00, wat);

        while (candidate <= now
               || candidate.DayOfWeek != DayOfWeek.Friday
               || !IsFortnightHolidayFree(calendar, candidate))
        {
            candidate = candidate.AddDays(1);
        }

        return candidate;
    }

    /// <summary>
    /// True when every Monday-to-Friday in the fourteen days after
    /// <paramref name="from"/> is a working day — i.e. no declared holiday
    /// falls inside the retirement window.
    /// </summary>
    private static bool IsFortnightHolidayFree(IWorkingCalendar calendar, DateTimeOffset from)
    {
        for (var offset = 1; offset <= 14; offset++)
        {
            var day = DateOnly.FromDateTime(from.AddDays(offset).Date);

            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            if (!calendar.IsWorkingDay(day))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Runs the real function, against the real database, through the real
    /// SQL application lock. Constructing RetirementSweep directly rather
    /// than going through the Functions host keeps the test to the behaviour
    /// under examination — the host's timer scheduling is Azure's concern,
    /// the sweep's arithmetic and audit writes are ours.
    /// </summary>
    private async Task RunSweepAtAsync(DateTimeOffset now)
    {
        Fixture.TimeProvider.SetUtcNow(now);

        using var scope = Fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Workflow.Infrastructure.Persistence.WorkflowDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<IWorkflowClock>();

        var sweep = new RetirementSweep(db, clock, NullLogger<RetirementSweep>.Instance);
        await sweep.RunAsync(new TimerInfo(), CancellationToken.None);
    }

    private async Task AssertStatusAsync(Guid advanceId, RetirementStatus expected)
    {
        var actual = await WithDbAsync(async db =>
            (await db.CashAdvanceRequests.SingleAsync(a => a.RequestId == advanceId)).RetirementStatus);

        actual.Should().Be(expected);
    }
}
