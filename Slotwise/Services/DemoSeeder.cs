using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Slotwise.Data;
using Slotwise.Domain;

namespace Slotwise.Services;

public sealed record DemoSeedResult(
    bool TenantCreated, Tenant Tenant,
    IReadOnlyList<Service> Services, IReadOnlyList<StaffMember> Staff);

/// <summary>
/// Seeds the demo tenant ("Lakeside Dental") used by the showcase: two
/// services, two staff with Mon–Sat hours, one admin login, and a spread
/// of bookings across today and tomorrow.
///
/// Idempotent: safe to call repeatedly. A missing tenant is created in
/// full; an existing tenant gets any missing admin / bookings topped up.
/// Used both by the startup seed-on-empty and by POST /api/dev/seed.
/// </summary>
public class DemoSeeder
{
    public const string DemoSlug = "lakeside-dental";
    public const string DemoAdminEmail = "admin@lakeside-dental.test";

    // Demo-only credential, documented in STAGE2-NOTES.md. The stored value
    // is a PasswordHasher hash; this constant exists so the seeder (and the
    // notes) have one source of truth.
    public const string DemoAdminPassword = "SlotwiseDemo1";

    private readonly SlotwiseDbContext _db;
    private readonly ITenantContext _tenantCtx;
    private readonly IPasswordHasher<TenantAdmin> _passwordHasher;
    private readonly TimeProvider _time;

    public DemoSeeder(
        SlotwiseDbContext db, ITenantContext tenantCtx,
        IPasswordHasher<TenantAdmin> passwordHasher, TimeProvider time)
    {
        _db = db;
        _tenantCtx = tenantCtx;
        _passwordHasher = passwordHasher;
        _time = time;
    }

    public async Task<DemoSeedResult> SeedAsync(CancellationToken ct = default)
    {
        var tenant = await _db.Tenants.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Slug == DemoSlug, ct);
        var created = false;

        if (tenant is null)
        {
            tenant = new Tenant
            {
                Id = Guid.NewGuid(),
                Name = "Lakeside Dental",
                Slug = DemoSlug,
                TimeZoneId = "Asia/Singapore",
                CreatedAt = _time.GetUtcNow(),
            };
            _db.Tenants.Add(tenant);
            await _db.SaveChangesAsync(ct);
            created = true;
        }

        // Everything below is tenant-scoped through the query filters.
        _tenantCtx.TenantId = tenant.Id;

        var services = await _db.Services.OrderBy(s => s.Name).ToListAsync(ct);
        if (services.Count == 0)
        {
            services =
            [
                new Service { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Checkup", DurationMinutes = 30, PriceCents = 5000 },
                new Service { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Cleaning", DurationMinutes = 60, PriceCents = 8000 },
            ];
            _db.Services.AddRange(services);
            await _db.SaveChangesAsync(ct);
        }

        var staff = await _db.StaffMembers.OrderBy(s => s.Name).ToListAsync(ct);
        if (staff.Count == 0)
        {
            staff =
            [
                new StaffMember { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Dr. Chen" },
                new StaffMember { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Dr. Patel" },
            ];
            _db.StaffMembers.AddRange(staff);

            foreach (var member in staff)
            foreach (var day in new[]
            {
                DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday,
            })
            {
                _db.WorkingHours.Add(new WorkingHours
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenant.Id,
                    StaffMemberId = member.Id,
                    DayOfWeek = day,
                    StartTime = new TimeOnly(9, 0),
                    EndTime = new TimeOnly(18, 0),
                });
            }
            await _db.SaveChangesAsync(ct);
        }

        if (!await _db.TenantAdmins.AnyAsync(ct))
        {
            var admin = new TenantAdmin
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                Email = DemoAdminEmail,
                PasswordHash = "",
                CreatedAt = _time.GetUtcNow(),
            };
            admin.PasswordHash = _passwordHasher.HashPassword(admin, DemoAdminPassword);
            _db.TenantAdmins.Add(admin);
            await _db.SaveChangesAsync(ct);
        }

        if (!await _db.Bookings.AnyAsync(ct))
        {
            await SeedBookingsAsync(tenant, services, staff, ct);
        }

        return new DemoSeedResult(created, tenant, services, staff);
    }

    /// <summary>
    /// ~8 bookings across today and tomorrow (tenant-local). Inserted
    /// directly (demo data), with a per-staff overlap guard for Confirmed
    /// entries so the set is always consistent. At least one Confirmed
    /// booking starts within the reminder window (2h) so the reminder
    /// worker has something to mark on its first scan.
    /// </summary>
    private async Task SeedBookingsAsync(
        Tenant tenant, IReadOnlyList<Service> services,
        IReadOnlyList<StaffMember> staff, CancellationToken ct)
    {
        var checkup = services.First(s => s.Name == "Checkup");
        var cleaning = services.First(s => s.Name == "Cleaning");
        var chen = staff.First(s => s.Name == "Dr. Chen");
        var patel = staff.First(s => s.Name == "Dr. Patel");

        var tz = TimeZoneInfo.FindSystemTimeZoneById(tenant.TimeZoneId);
        var nowUtc = _time.GetUtcNow();
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, tz);
        var today = DateOnly.FromDateTime(localNow.DateTime);
        var tomorrow = today.AddDays(1);

        DateTimeOffset Local(DateOnly date, int hour, int minute) =>
            new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(new TimeOnly(hour, minute)), tz));

        var planned = new List<(Service Service, StaffMember Staff, DateTimeOffset Start,
            BookingStatus Status, string Name, string Contact)>();

        // 1) Due soon: the next half-hour boundary at least 10 minutes from
        //    now — always inside the 2-hour reminder window.
        var dueLocal = localNow.AddMinutes(30 - localNow.Minute % 30);
        if (dueLocal - localNow < TimeSpan.FromMinutes(10))
            dueLocal = dueLocal.AddMinutes(30);
        var dueStart = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(dueLocal.DateTime, tz));
        planned.Add((checkup, chen, dueStart, BookingStatus.Confirmed,
            "Aisha Rahman", "aisha@example.com"));

        // 2) Later today, if it still lands inside working hours.
        var laterLocal = localNow.AddHours(3);
        var laterStart = Local(today, laterLocal.Hour, 0);
        if (laterLocal.Date == localNow.Date && laterLocal.Hour is >= 9 and <= 17)
            planned.Add((cleaning, patel, laterStart, BookingStatus.Confirmed,
                "Wei Jie Tan", "+65 9123 4567"));

        // 3) Earlier today, already completed (only if 09:30 is past).
        var morningStart = Local(today, 9, 30);
        if (morningStart < nowUtc)
            planned.Add((checkup, chen, morningStart, BookingStatus.Completed,
                "Priya Nair", "priya@example.com"));

        // 4) A cancelled booking today (shows the state, blocks nothing).
        planned.Add((checkup, patel, Local(today, 15, 0), BookingStatus.Cancelled,
            "Marcus Lee", "marcus@example.com"));

        // 5–8) Tomorrow's spread.
        planned.Add((cleaning, chen, Local(tomorrow, 9, 30), BookingStatus.Confirmed,
            "Sarah Lim", "sarah@example.com"));
        planned.Add((checkup, patel, Local(tomorrow, 11, 0), BookingStatus.Confirmed,
            "Daniel Koh", "+65 9876 5432"));
        planned.Add((checkup, chen, Local(tomorrow, 14, 30), BookingStatus.Confirmed,
            "Mei Ling Ng", "meiling@example.com"));
        planned.Add((cleaning, patel, Local(tomorrow, 16, 0), BookingStatus.Confirmed,
            "Arun Kumar", "arun@example.com"));

        var confirmedRanges = new List<(Guid StaffId, DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var p in planned)
        {
            var end = p.Start.AddMinutes(p.Service.DurationMinutes);
            if (p.Status == BookingStatus.Confirmed &&
                confirmedRanges.Any(r => r.StaffId == p.Staff.Id && r.Start < end && p.Start < r.End))
                continue; // would collide with an already-planned booking
            if (p.Status == BookingStatus.Confirmed)
                confirmedRanges.Add((p.Staff.Id, p.Start, end));

            _db.Bookings.Add(new Booking
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                ServiceId = p.Service.Id,
                StaffMemberId = p.Staff.Id,
                CustomerName = p.Name,
                CustomerContact = p.Contact,
                StartUtc = p.Start,
                EndUtc = end,
                Status = p.Status,
                CreatedAt = nowUtc,
            });
        }
        await _db.SaveChangesAsync(ct);
    }
}
