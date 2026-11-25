using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Slotwise.Data;
using Slotwise.Domain;

namespace Slotwise.Pages.Admin;

/// <summary>
/// Base for admin pages. The tenant context is already set from the auth
/// cookie's tenant claim (TenantClaimMiddleware), so every query through
/// <see cref="Db"/> is scoped to the logged-in admin's tenant by the EF
/// global query filters — page code never passes a TenantId around.
/// </summary>
public abstract class AdminPageModel : PageModel
{
    protected AdminPageModel(SlotwiseDbContext db, TimeProvider time)
    {
        Db = db;
        Time = time;
    }

    protected SlotwiseDbContext Db { get; }
    protected TimeProvider Time { get; }

    private Tenant? _tenant;
    private TimeZoneInfo? _tz;

    protected async Task<Tenant> TenantAsync() =>
        _tenant ??= await Db.Tenants.FirstAsync();

    protected async Task<TimeZoneInfo> TzAsync()
    {
        if (_tz is null)
        {
            var tenant = await TenantAsync();
            _tz = TimeZoneInfo.FindSystemTimeZoneById(tenant.TimeZoneId);
        }
        return _tz;
    }

    protected static DateTimeOffset ToLocal(DateTimeOffset utc, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTime(utc, tz);

    /// <summary>The UTC range [start, end) covering one tenant-local day.</summary>
    protected static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) LocalDayRange(
        DateOnly day, TimeZoneInfo tz)
    {
        var start = new DateTimeOffset(
            TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(TimeOnly.MinValue), tz));
        return (start, start.AddDays(1));
    }

    protected async Task<(Dictionary<Guid, string> Services, Dictionary<Guid, string> Staff)>
        NameMapsAsync()
    {
        var services = await Db.Services.ToDictionaryAsync(s => s.Id, s => s.Name);
        var staff = await Db.StaffMembers.ToDictionaryAsync(s => s.Id, s => s.Name);
        return (services, staff);
    }
}
