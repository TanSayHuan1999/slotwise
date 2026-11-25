using Microsoft.EntityFrameworkCore;
using Npgsql;
using Slotwise.Data;
using Slotwise.Domain;

namespace Slotwise.Services;

public enum BookingOutcome
{
    Success,
    SlotTaken,
    OutsideHours,
    InPast,
    NotFound,
}

public sealed record BookingResult(BookingOutcome Outcome, Booking? Booking)
{
    public static BookingResult Fail(BookingOutcome outcome) => new(outcome, null);
}

public enum CancelOutcome
{
    Cancelled,
    NotFound,
    NotCancellable,
}

/// <summary>An available start time, in UTC and in the tenant's local time.</summary>
public sealed record SlotInfo(DateTimeOffset StartUtc, DateTimeOffset LocalStart);

public sealed record SlotResult(bool Found, IReadOnlyList<SlotInfo> Slots);

public class BookingService
{
    private const int SlotStepMinutes = 15;

    private readonly SlotwiseDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly TimeProvider _time;

    public BookingService(SlotwiseDbContext db, ITenantContext tenant, TimeProvider time)
    {
        _db = db;
        _tenant = tenant;
        _time = time;
    }

    /// <summary>
    /// Creates a booking for the current tenant. Expected failures are
    /// returned as typed outcomes, never thrown.
    ///
    /// The no-double-booking guarantee is enforced twice:
    ///  1. here — an overlap check against Confirmed bookings inside a
    ///     transaction, and
    ///  2. in the database — a PostgreSQL exclusion constraint over
    ///     tstzrange(StartUtc, EndUtc) for Confirmed bookings. Two racing
    ///     transactions can both pass check (1) under READ COMMITTED; the
    ///     loser's INSERT then fails with SQLSTATE 23P01, which we translate
    ///     to SlotTaken.
    /// </summary>
    public async Task<BookingResult> CreateAsync(
        Guid serviceId,
        Guid staffId,
        string customerName,
        string customerContact,
        DateTimeOffset startUtc,
        CancellationToken ct = default)
    {
        if (_tenant.TenantId is not { } tenantId)
            return BookingResult.Fail(BookingOutcome.NotFound);

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        if (tenant is null)
            return BookingResult.Fail(BookingOutcome.NotFound);

        // Query filters scope these lookups to the current tenant, so an id
        // belonging to another tenant simply looks like it does not exist.
        var service = await _db.Services.FirstOrDefaultAsync(s => s.Id == serviceId, ct);
        if (service is null || !service.IsActive)
            return BookingResult.Fail(BookingOutcome.NotFound);

        var staff = await _db.StaffMembers.FirstOrDefaultAsync(s => s.Id == staffId, ct);
        if (staff is null || !staff.IsActive)
            return BookingResult.Fail(BookingOutcome.NotFound);

        var now = _time.GetUtcNow();
        if (startUtc < now)
            return BookingResult.Fail(BookingOutcome.InPast);

        var endUtc = startUtc.AddMinutes(service.DurationMinutes);

        var tz = TimeZoneInfo.FindSystemTimeZoneById(tenant.TimeZoneId);
        if (!await FitsWorkingHoursAsync(staffId, tz, startUtc, endUtc, ct))
            return BookingResult.Fail(BookingOutcome.OutsideHours);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        var overlaps = await _db.Bookings.AnyAsync(b =>
            b.StaffMemberId == staffId &&
            b.Status == BookingStatus.Confirmed &&
            b.StartUtc < endUtc && startUtc < b.EndUtc, ct);
        if (overlaps)
            return BookingResult.Fail(BookingOutcome.SlotTaken);

        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ServiceId = serviceId,
            StaffMemberId = staffId,
            CustomerName = customerName,
            CustomerContact = customerContact,
            StartUtc = startUtc,
            EndUtc = endUtc,
            Status = BookingStatus.Confirmed,
            CreatedAt = now,
        };
        _db.Bookings.Add(booking);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsExclusionViolation(ex))
        {
            // Lost a race: another transaction committed an overlapping
            // Confirmed booking first and the exclusion constraint fired.
            return BookingResult.Fail(BookingOutcome.SlotTaken);
        }

        await tx.CommitAsync(ct);
        return new BookingResult(BookingOutcome.Success, booking);
    }

    public async Task<CancelOutcome> CancelAsync(Guid bookingId, CancellationToken ct = default)
    {
        var booking = await _db.Bookings.FirstOrDefaultAsync(b => b.Id == bookingId, ct);
        if (booking is null)
            return CancelOutcome.NotFound;
        if (booking.Status != BookingStatus.Confirmed)
            return CancelOutcome.NotCancellable;

        booking.Status = BookingStatus.Cancelled;
        await _db.SaveChangesAsync(ct);
        return CancelOutcome.Cancelled;
    }

    /// <summary>
    /// Available start times for a service + staff member on a date given in
    /// the tenant's timezone. Candidates step every 15 minutes from each
    /// working-hours window start; a candidate is available when the whole
    /// [start, start+duration) fits the window, is not in the past, and does
    /// not overlap a Confirmed booking.
    /// </summary>
    public async Task<SlotResult> GetSlotsAsync(
        Guid serviceId, Guid staffId, DateOnly date, CancellationToken ct = default)
    {
        if (_tenant.TenantId is not { } tenantId)
            return new SlotResult(false, []);

        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct);
        var service = await _db.Services.FirstOrDefaultAsync(s => s.Id == serviceId, ct);
        var staff = await _db.StaffMembers.FirstOrDefaultAsync(s => s.Id == staffId, ct);
        if (tenant is null || service is null || !service.IsActive || staff is null || !staff.IsActive)
            return new SlotResult(false, []);

        var tz = TimeZoneInfo.FindSystemTimeZoneById(tenant.TimeZoneId);

        var windows = await _db.WorkingHours
            .Where(w => w.StaffMemberId == staffId && w.DayOfWeek == date.DayOfWeek)
            .OrderBy(w => w.StartTime)
            .ToListAsync(ct);
        if (windows.Count == 0)
            return new SlotResult(true, []);

        // Fetch the day's Confirmed bookings (generous UTC window; the
        // overlap test itself is exact).
        var localDayStart = date.ToDateTime(TimeOnly.MinValue);
        var utcDayStart = TimeZoneInfo.ConvertTimeToUtc(localDayStart, tz);
        var dayBookings = await _db.Bookings
            .Where(b => b.StaffMemberId == staffId &&
                        b.Status == BookingStatus.Confirmed &&
                        b.StartUtc < utcDayStart.AddDays(2) && b.EndUtc > utcDayStart)
            .ToListAsync(ct);

        var now = _time.GetUtcNow();
        var duration = TimeSpan.FromMinutes(service.DurationMinutes);
        var slots = new List<SlotInfo>();

        foreach (var window in windows)
        {
            for (var t = window.StartTime;
                 t.AddMinutes(service.DurationMinutes) <= window.EndTime;
                 t = t.AddMinutes(SlotStepMinutes))
            {
                var local = date.ToDateTime(t);
                var startUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, tz));
                var endUtc = startUtc + duration;

                if (startUtc <= now)
                    continue;
                if (dayBookings.Any(b => b.StartUtc < endUtc && startUtc < b.EndUtc))
                    continue;

                slots.Add(new SlotInfo(startUtc, new DateTimeOffset(local, tz.GetUtcOffset(local))));
            }
        }

        return new SlotResult(true, slots.OrderBy(s => s.StartUtc).ToList());
    }

    /// <summary>
    /// Confirmed bookings starting within the next <paramref name="withinHours"/>
    /// hours that have not had a reminder sent yet. Used by the stage-2
    /// reminder worker.
    ///
    /// This deliberately uses IgnoreQueryFilters: the reminder scan is a
    /// cross-tenant background job by design — there is no "current tenant"
    /// when it runs. Each returned booking carries its TenantId so the
    /// worker can act per tenant.
    /// </summary>
    public async Task<IReadOnlyList<Booking>> GetUpcomingRemindersAsync(
        int withinHours, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        var until = now.AddHours(withinHours);

        return await _db.Bookings
            .IgnoreQueryFilters()
            .Where(b => b.Status == BookingStatus.Confirmed &&
                        b.ReminderSentAt == null &&
                        b.StartUtc > now && b.StartUtc <= until)
            .OrderBy(b => b.StartUtc)
            .ToListAsync(ct);
    }

    private async Task<bool> FitsWorkingHoursAsync(
        Guid staffId, TimeZoneInfo tz, DateTimeOffset startUtc, DateTimeOffset endUtc,
        CancellationToken ct)
    {
        var localStart = TimeZoneInfo.ConvertTime(startUtc, tz);
        var localEnd = TimeZoneInfo.ConvertTime(endUtc, tz);

        // Bookings never span local midnight in v1.
        if (localStart.Date != localEnd.Date)
            return false;

        var windows = await _db.WorkingHours
            .Where(w => w.StaffMemberId == staffId && w.DayOfWeek == localStart.DayOfWeek)
            .ToListAsync(ct);

        var startTime = TimeOnly.FromDateTime(localStart.DateTime);
        var endTime = TimeOnly.FromDateTime(localEnd.DateTime);
        return windows.Any(w => w.StartTime <= startTime && endTime <= w.EndTime);
    }

    private static bool IsExclusionViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: "23P01" };
}
