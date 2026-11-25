using Microsoft.EntityFrameworkCore;
using Slotwise.Data;
using Slotwise.Domain;

namespace Slotwise.Pages.Admin;

public class IndexModel : AdminPageModel
{
    public IndexModel(SlotwiseDbContext db, TimeProvider time) : base(db, time)
    {
    }

    public Tenant Tenant { get; private set; } = null!;
    public TimeZoneInfo Tz { get; private set; } = null!;
    public DateOnly TodayLocal { get; private set; }
    public IReadOnlyList<Booking> TodayBookings { get; private set; } = [];
    public IReadOnlyList<Booking> Upcoming { get; private set; } = [];
    public int UpcomingCount { get; private set; }
    public int RemindersSentCount { get; private set; }
    public Dictionary<Guid, string> ServiceNames { get; private set; } = new();
    public Dictionary<Guid, string> StaffNames { get; private set; } = new();

    /// <summary>Converts a UTC instant to tenant-local time (for views).</summary>
    public DateTimeOffset Local(DateTimeOffset utc) => ToLocal(utc, Tz);

    public async Task OnGetAsync()
    {
        Tenant = await TenantAsync();
        Tz = await TzAsync();
        var now = Time.GetUtcNow();

        TodayLocal = DateOnly.FromDateTime(ToLocal(now, Tz).DateTime);
        var (dayStart, dayEnd) = LocalDayRange(TodayLocal, Tz);
        TodayBookings = await Db.Bookings
            .Where(b => b.StartUtc >= dayStart && b.StartUtc < dayEnd)
            .OrderBy(b => b.StartUtc)
            .ToListAsync();

        Upcoming = await Db.Bookings
            .Where(b => b.Status == BookingStatus.Confirmed && b.StartUtc > now)
            .OrderBy(b => b.StartUtc)
            .Take(10)
            .ToListAsync();
        UpcomingCount = await Db.Bookings
            .CountAsync(b => b.Status == BookingStatus.Confirmed && b.StartUtc > now);
        RemindersSentCount = await Db.Bookings
            .CountAsync(b => b.ReminderSentAt != null);

        (ServiceNames, StaffNames) = await NameMapsAsync();
    }
}
