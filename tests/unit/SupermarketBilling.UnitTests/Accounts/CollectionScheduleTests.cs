using SupermarketBilling.Domain.Accounts;
using SupermarketBilling.Domain.Common;

namespace SupermarketBilling.UnitTests.Accounts;

public sealed class CollectionScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 6, 0, 0, TimeSpan.Zero);
    private static readonly CollectionPlan.Assignment NoOne = new(null, null, null, null, null, null);

    private static CollectionPlan Plan(CollectionPlan.Schedule schedule)
    {
        var plan = CollectionPlan.For(Guid.NewGuid(), Guid.NewGuid());
        plan.Set(NoOne, schedule, Now);
        return plan;
    }

    private static List<DateOnly> Days(CollectionPlan plan, DateOnly from, int days, params DateOnly[] dues) =>
        Enumerable.Range(0, days).Select(from.AddDays).Where(d => plan.IsScheduledOn(d, dues)).ToList();

    [Fact]
    public void Weekday_schedules_visit_on_the_chosen_days()
    {
        var plan = Plan(new(ScheduleTypes.Weekdays, [DayOfWeek.Monday, DayOfWeek.Thursday]));
        Assert.Equal([new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 8), new DateOnly(2026, 10, 12)], Days(plan, new DateOnly(2026, 10, 4), 9));
    }

    [Fact]
    public void Fortnightly_visits_every_second_week_from_the_anchor_week_in_both_directions()
    {
        // Anchor: Friday 2 October 2026; visits on Tuesdays of that week and every second week.
        var plan = Plan(new(ScheduleTypes.Fortnightly, [DayOfWeek.Tuesday], AnchorDate: new DateOnly(2026, 10, 2)));
        Assert.Equal(
            [new DateOnly(2026, 9, 15), new DateOnly(2026, 9, 29), new DateOnly(2026, 10, 13), new DateOnly(2026, 10, 27)],
            Days(plan, new DateOnly(2026, 9, 10), 50));
    }

    [Fact]
    public void Monthly_visits_fall_back_to_the_last_day_of_a_short_month()
    {
        var plan = Plan(new(ScheduleTypes.Monthly, MonthDay: 31));
        Assert.Equal([new DateOnly(2027, 1, 31), new DateOnly(2027, 2, 28), new DateOnly(2027, 3, 31)], Days(plan, new DateOnly(2027, 1, 1), 90));
    }

    [Fact]
    public void Due_date_schedules_follow_the_unpaid_invoices()
    {
        var before = Plan(new(ScheduleTypes.DueDate, DueOffsetDays: -2));
        var after = Plan(new(ScheduleTypes.DueDate, DueOffsetDays: 3));
        var due = new DateOnly(2026, 10, 15);
        Assert.Equal([new DateOnly(2026, 10, 13)], Days(before, new DateOnly(2026, 10, 1), 30, due));
        Assert.Equal([new DateOnly(2026, 10, 18)], Days(after, new DateOnly(2026, 10, 1), 30, due));
        Assert.Empty(Days(after, new DateOnly(2026, 10, 1), 30)); // nothing unpaid, no visit
    }

    [Fact]
    public void Plans_are_checked_when_set()
    {
        var plan = CollectionPlan.For(Guid.NewGuid(), Guid.NewGuid());
        var collector = Guid.NewGuid();
        Assert.Equal("plan.backup_same", Assert.Throws<DomainException>(() =>
            plan.Set(NoOne with { PrimaryCollectorUserId = collector, BackupCollectorUserId = collector }, new(ScheduleTypes.Manual), Now)).Code);
        Assert.Equal("plan.weekdays_required", Assert.Throws<DomainException>(() => plan.Set(NoOne, new(ScheduleTypes.Weekdays), Now)).Code);
        Assert.Equal("plan.time_invalid", Assert.Throws<DomainException>(() =>
            plan.Set(NoOne with { PreferredFrom = new TimeOnly(12, 0), PreferredTo = new TimeOnly(10, 0) }, new(ScheduleTypes.Manual), Now)).Code);
        Assert.False(Plan(new(ScheduleTypes.Manual)).IsScheduledOn(new DateOnly(2026, 10, 2), []));
        Assert.Equal("promise.date_past", Assert.Throws<DomainException>(() =>
            PaymentPromise.Record(Guid.NewGuid(), Guid.NewGuid(), 100m, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2), null, Guid.NewGuid(), Now)).Code);
    }
}
