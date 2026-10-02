using System.Text.RegularExpressions;
using SupermarketBilling.Domain.Common;
using SupermarketBilling.Domain.Tenancy;

namespace SupermarketBilling.Domain.Accounts;

/// <summary>A collection route (a round of parties visited together, in sequence).</summary>
public sealed partial class Route : ITenantOwned
{
    private Route()
    {
        Code = Name = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public string Code { get; private set; }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public uint RowVersion { get; private set; }

    public static Route Create(Guid businessId, string code, string name, string? description, DateTimeOffset now)
    {
        var route = new Route
        {
            Id = Guid.CreateVersion7(now),
            BusinessId = businessId,
            Code = (code ?? string.Empty).Trim().ToUpperInvariant() is var c && CodePattern().IsMatch(c)
                ? c
                : throw new DomainException("route.code_invalid", "A route code is 1-20 letters, digits or hyphens."),
            IsActive = true,
            CreatedAtUtc = now,
        };
        route.Update(name, description, true);
        return route;
    }

    public void Update(string name, string? description, bool isActive)
    {
        Name = (name ?? string.Empty).Trim() is { Length: > 0 and <= 100 } n ? n : throw new DomainException("route.name_required", "Give the route a name (max 100 characters).");
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim() is { Length: <= 300 } d ? d
            : throw new DomainException("route.description_invalid", "A description is at most 300 characters.");
        IsActive = isActive;
    }

    [GeneratedRegex("^[A-Z0-9-]{1,20}$")]
    private static partial Regex CodePattern();
}

public static class ScheduleTypes
{
    /// <summary>Not scheduled: visited only when a manager assigns a visit (or a promise falls due).</summary>
    public const string Manual = "MANUAL";

    /// <summary>On one or more weekdays every week.</summary>
    public const string Weekdays = "WEEKDAYS";

    /// <summary>Every second week on one weekday, counted from an anchor date.</summary>
    public const string Fortnightly = "FORTNIGHTLY";

    /// <summary>On a day of the month (the last day when the month is shorter).</summary>
    public const string Monthly = "MONTHLY";

    /// <summary>When an unpaid invoice falls due, shifted by a number of days (negative: before the due date).</summary>
    public const string DueDate = "DUE_DATE";

    /// <summary>On one specific date.</summary>
    public const string SpecificDate = "SPECIFIC_DATE";

    public static readonly IReadOnlyList<string> All = [Manual, Weekdays, Fortnightly, Monthly, DueDate, SpecificDate];
}

/// <summary>
/// How and when a debtor is visited for collection (spec section 14): route and position on it, the primary and
/// backup collector, the preferred time, and the schedule. One per debtor; changes are audited.
/// </summary>
public sealed class CollectionPlan : ITenantOwned
{
    private CollectionPlan()
    {
        ScheduleType = ScheduleTypes.Manual;
    }

    public Guid DebtorId { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid? RouteId { get; private set; }

    public int? VisitSequence { get; private set; }

    public Guid? PrimaryCollectorUserId { get; private set; }

    public Guid? BackupCollectorUserId { get; private set; }

    public TimeOnly? PreferredFrom { get; private set; }

    public TimeOnly? PreferredTo { get; private set; }

    public string ScheduleType { get; private set; }

    /// <summary>For <see cref="ScheduleTypes.Weekdays"/> and <see cref="ScheduleTypes.Fortnightly"/>: bit 0 = Sunday ... bit 6 = Saturday.</summary>
    public int WeekdayMask { get; private set; }

    /// <summary>Fortnightly: a date in a visiting week. Specific date: the visit date.</summary>
    public DateOnly? AnchorDate { get; private set; }

    public int? MonthDay { get; private set; }

    /// <summary>Due-date based: days after the due date (negative: before).</summary>
    public int? DueOffsetDays { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public uint RowVersion { get; private set; }

    public sealed record Schedule(string Type, IReadOnlyCollection<DayOfWeek>? Weekdays = null, DateOnly? AnchorDate = null, int? MonthDay = null, int? DueOffsetDays = null);

    public sealed record Assignment(Guid? RouteId, int? VisitSequence, Guid? PrimaryCollectorUserId, Guid? BackupCollectorUserId, TimeOnly? PreferredFrom, TimeOnly? PreferredTo);

    public static CollectionPlan For(Guid businessId, Guid debtorId) => new() { BusinessId = businessId, DebtorId = debtorId };

    public void Set(Assignment assignment, Schedule schedule, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(schedule);
        if (assignment.VisitSequence is < 1 or > 9999)
        {
            throw new DomainException("plan.sequence_invalid", "The visit sequence is 1 to 9999.");
        }

        if (assignment.PrimaryCollectorUserId is { } p && p == assignment.BackupCollectorUserId)
        {
            throw new DomainException("plan.backup_same", "The backup collector must be someone other than the primary collector.");
        }

        if (assignment.PreferredFrom is { } from && assignment.PreferredTo is { } to && to <= from)
        {
            throw new DomainException("plan.time_invalid", "The preferred time must end after it starts.");
        }

        var mask = (schedule.Weekdays ?? []).Aggregate(0, (m, d) => m | (1 << (int)d));
        switch (schedule.Type)
        {
            case ScheduleTypes.Weekdays when mask == 0:
                throw new DomainException("plan.weekdays_required", "Choose at least one weekday.");
            case ScheduleTypes.Fortnightly when schedule.AnchorDate is null || (schedule.Weekdays ?? []).Count != 1:
                throw new DomainException("plan.fortnight_invalid", "A fortnightly visit needs one weekday and a date in a visiting week.");
            case ScheduleTypes.Monthly when schedule.MonthDay is not (>= 1 and <= 31):
                throw new DomainException("plan.month_day_invalid", "A monthly visit needs a day of the month (1-31).");
            case ScheduleTypes.DueDate when schedule.DueOffsetDays is not (>= -60 and <= 60):
                throw new DomainException("plan.due_offset_invalid", "Days before or after the due date: -60 to 60.");
            case ScheduleTypes.SpecificDate when schedule.AnchorDate is null:
                throw new DomainException("plan.date_required", "Choose the visit date.");
            case var t when !ScheduleTypes.All.Contains(t):
                throw new DomainException("plan.schedule_invalid", $"Unknown schedule '{t}'.");
        }

        (RouteId, VisitSequence, PrimaryCollectorUserId, BackupCollectorUserId, PreferredFrom, PreferredTo) =
            (assignment.RouteId, assignment.VisitSequence, assignment.PrimaryCollectorUserId, assignment.BackupCollectorUserId, assignment.PreferredFrom, assignment.PreferredTo);
        ScheduleType = schedule.Type;
        WeekdayMask = schedule.Type is ScheduleTypes.Weekdays or ScheduleTypes.Fortnightly ? mask : 0;
        AnchorDate = schedule.Type is ScheduleTypes.Fortnightly or ScheduleTypes.SpecificDate ? schedule.AnchorDate : null;
        MonthDay = schedule.Type == ScheduleTypes.Monthly ? schedule.MonthDay : null;
        DueOffsetDays = schedule.Type == ScheduleTypes.DueDate ? schedule.DueOffsetDays : null;
        UpdatedAtUtc = now;
    }

    public IReadOnlyList<DayOfWeek> Weekdays => Enum.GetValues<DayOfWeek>().Where(d => (WeekdayMask & (1 << (int)d)) != 0).ToList();

    /// <summary>Whether the schedule calls for a visit on <paramref name="date"/>, given the due dates of the debtor's unpaid charges.</summary>
    public bool IsScheduledOn(DateOnly date, IEnumerable<DateOnly> unpaidDueDates)
    {
        ArgumentNullException.ThrowIfNull(unpaidDueDates);
        var onWeekday = (WeekdayMask & (1 << (int)date.DayOfWeek)) != 0;
        return ScheduleType switch
        {
            ScheduleTypes.Weekdays => onWeekday,
            ScheduleTypes.Fortnightly => onWeekday && AnchorDate is { } anchor && WeeksBetween(anchor, date) % 2 == 0,
            ScheduleTypes.Monthly => MonthDay is { } day && date.Day == Math.Min(day, DateTime.DaysInMonth(date.Year, date.Month)),
            ScheduleTypes.DueDate => DueOffsetDays is { } offset && unpaidDueDates.Any(d => d.AddDays(offset) == date),
            ScheduleTypes.SpecificDate => AnchorDate == date,
            _ => false,
        };
    }

    /// <summary>Whole weeks (Sunday to Saturday) from the week of <paramref name="from"/> to the week of <paramref name="to"/>, never negative.</summary>
    private static int WeeksBetween(DateOnly from, DateOnly to)
    {
        static int WeekStart(DateOnly d) => d.DayNumber - (int)d.DayOfWeek;
        return Math.Abs(WeekStart(to) - WeekStart(from)) / 7;
    }
}

/// <summary>A visit a manager assigns to a collector for a day (on top of, or instead of, the schedule).</summary>
public sealed class CollectionVisit : ITenantOwned
{
    private CollectionVisit()
    {
        Note = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid DebtorId { get; private set; }

    public Guid CollectorUserId { get; private set; }

    public DateOnly VisitDate { get; private set; }

    public string Note { get; private set; }

    public bool IsCancelled { get; private set; }

    public Guid AssignedByUserId { get; private set; }

    public DateTimeOffset AssignedAtUtc { get; private set; }

    public static CollectionVisit Assign(Guid businessId, Guid debtorId, Guid collector, DateOnly date, DateOnly today, string? note, Guid assignedBy, DateTimeOffset now) =>
        date < today
            ? throw new DomainException("visit.date_past", "A visit cannot be assigned for a past day.")
            : new CollectionVisit
            {
                Id = Guid.CreateVersion7(now),
                BusinessId = businessId,
                DebtorId = debtorId,
                CollectorUserId = collector,
                VisitDate = date,
                Note = (note ?? string.Empty).Trim() is { Length: <= 300 } n ? n : throw new DomainException("visit.note_invalid", "A note is at most 300 characters."),
                AssignedByUserId = assignedBy,
                AssignedAtUtc = now,
            };

    public void Cancel() => IsCancelled = true;
}

/// <summary>A debtor's promise to pay an amount by a date (recorded by a collector or the office). Whether it was kept is worked out from receipts.</summary>
public sealed class PaymentPromise : ITenantOwned
{
    private PaymentPromise()
    {
        Note = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid DebtorId { get; private set; }

    public decimal Amount { get; private set; }

    public DateOnly PromisedDate { get; private set; }

    public string Note { get; private set; }

    public bool IsCancelled { get; private set; }

    public Guid RecordedByUserId { get; private set; }

    public DateTimeOffset RecordedAtUtc { get; private set; }

    public static PaymentPromise Record(Guid businessId, Guid debtorId, decimal amount, DateOnly date, DateOnly today, string? note, Guid recordedBy, DateTimeOffset now)
    {
        if (amount <= 0 || decimal.Round(amount, 2) != amount)
        {
            throw new DomainException("promise.amount_invalid", "A promised amount is positive, in rupees and paise.");
        }

        return date < today
            ? throw new DomainException("promise.date_past", "A promise is for today or a later day.")
            : new PaymentPromise
            {
                Id = Guid.CreateVersion7(now),
                BusinessId = businessId,
                DebtorId = debtorId,
                Amount = amount,
                PromisedDate = date,
                Note = (note ?? string.Empty).Trim() is { Length: <= 300 } n ? n : throw new DomainException("promise.note_invalid", "A note is at most 300 characters."),
                RecordedByUserId = recordedBy,
                RecordedAtUtc = now,
            };
    }

    public void Cancel() => IsCancelled = true;
}

/// <summary>A collector who is away for a day: their parties go to the backup collectors that day.</summary>
public sealed class CollectorAbsence : ITenantOwned
{
    private CollectorAbsence()
    {
        Reason = string.Empty;
    }

    public Guid Id { get; private set; }

    public Guid BusinessId { get; private set; }

    public Guid CollectorUserId { get; private set; }

    public DateOnly AbsentOn { get; private set; }

    public string Reason { get; private set; }

    public Guid RecordedByUserId { get; private set; }

    public DateTimeOffset RecordedAtUtc { get; private set; }

    public static CollectorAbsence Record(Guid businessId, Guid collector, DateOnly date, string? reason, Guid recordedBy, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        BusinessId = businessId,
        CollectorUserId = collector,
        AbsentOn = date,
        Reason = (reason ?? string.Empty).Trim() is { Length: <= 200 } r ? r : throw new DomainException("absence.reason_invalid", "A reason is at most 200 characters."),
        RecordedByUserId = recordedBy,
        RecordedAtUtc = now,
    };
}

/// <summary>Why a party is on a collector's list for a day.</summary>
public static class VisitReasons
{
    public const string Schedule = "SCHEDULE";
    public const string DueDate = "DUE_DATE";
    public const string Promise = "PROMISE";
    public const string Assigned = "ASSIGNED";
    public const string Backup = "BACKUP";
    public const string Overdue = "OVERDUE";
}
