using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Slotwise.Data;
using Slotwise.Domain;
using Slotwise.Services;

namespace Slotwise.Pages.Book;

/// <summary>
/// The public booking page for one tenant: /book/{slug}.
///
/// One page, three states, all driven by query parameters so every step is
/// a plain GET link or a PRG redirect (no session, no JS framework):
///   1. select  — service / staff / date pickers + the available slots
///   2. confirm — ?slot=&lt;start&gt; adds a summary + name/contact form
///   3. done    — ?booked=&lt;id&gt; shows the confirmed booking
/// If the chosen slot is taken between viewing and submitting, the POST
/// redirects back to the select state with a friendly flash message and a
/// fresh slot list — never an error page.
/// </summary>
public class IndexModel : PageModel
{
    private readonly SlotwiseDbContext _db;
    private readonly ITenantContext _tenantCtx;
    private readonly BookingService _bookings;
    private readonly TimeProvider _time;

    public IndexModel(
        SlotwiseDbContext db, ITenantContext tenantCtx,
        BookingService bookings, TimeProvider time)
    {
        _db = db;
        _tenantCtx = tenantCtx;
        _bookings = bookings;
        _time = time;
    }

    public Tenant Tenant { get; private set; } = null!;
    public bool TenantMissing { get; private set; }

    /// <summary>All services/staff of the tenant (for name lookups).</summary>
    public IReadOnlyList<Service> AllServices { get; private set; } = [];
    public IReadOnlyList<StaffMember> AllStaff { get; private set; } = [];
    /// <summary>Active-only subsets shown in the pickers.</summary>
    public IReadOnlyList<Service> Services { get; private set; } = [];
    public IReadOnlyList<StaffMember> Staff { get; private set; } = [];

    public Guid? ServiceId { get; private set; }
    public Guid? StaffId { get; private set; }
    public DateOnly Date { get; private set; }
    public DateOnly MinDate { get; private set; }
    public DateOnly MaxDate { get; private set; }
    public IReadOnlyList<SlotInfo> Slots { get; private set; } = [];

    // Confirm state
    public SlotInfo? ConfirmSlot { get; private set; }
    public Service? ConfirmService { get; private set; }
    public StaffMember? ConfirmStaff { get; private set; }
    public string? FormError { get; private set; }

    // Done state
    public Booking? CompletedBooking { get; private set; }

    [TempData]
    public string? Flash { get; set; }

    // Confirm-form fields (round-tripped through hidden inputs).
    [BindProperty] public Guid FormServiceId { get; set; }
    [BindProperty] public Guid FormStaffId { get; set; }
    [BindProperty] public string FormDate { get; set; } = "";
    [BindProperty] public string FormSlot { get; set; } = "";
    [BindProperty] public string? FormName { get; set; }
    [BindProperty] public string? FormContact { get; set; }

    public async Task<IActionResult> OnGetAsync(
        string slug, Guid? serviceId, Guid? staffId, DateOnly? date,
        string? slot, Guid? booked)
    {
        if (!await LoadTenantAsync(slug))
        {
            TenantMissing = true;
            Response.StatusCode = StatusCodes.Status404NotFound;
            return Page();
        }

        var tz = TenantTz();
        MinDate = TenantToday(tz);
        MaxDate = MinDate.AddDays(14);
        Date = date is { } d ? d.Clamp(MinDate, MaxDate) : MinDate;

        ServiceId = serviceId is { } s && Services.Any(x => x.Id == s)
            ? s : Services.FirstOrDefault()?.Id;
        StaffId = staffId is { } m && Staff.Any(x => x.Id == m)
            ? m : Staff.FirstOrDefault()?.Id;

        if (booked is { } bookingId)
        {
            CompletedBooking = await _db.Bookings
                .FirstOrDefaultAsync(b => b.Id == bookingId);
            return Page();
        }

        if (ServiceId is { } svcId && StaffId is { } stfId)
        {
            var result = await _bookings.GetSlotsAsync(svcId, stfId, Date);
            Slots = result.Slots;

            if (slot is not null && DateTimeOffset.TryParse(slot, out var startUtc))
            {
                var match = Slots.FirstOrDefault(x => x.StartUtc == startUtc);
                if (match is not null)
                {
                    ConfirmSlot = match;
                    ConfirmService = Services.First(x => x.Id == svcId);
                    ConfirmStaff = Staff.First(x => x.Id == stfId);
                    FormServiceId = svcId;
                    FormStaffId = stfId;
                    FormDate = Date.ToString("yyyy-MM-dd");
                    FormSlot = slot;
                }
                else
                {
                    // The slot shown a moment ago is gone (booked by someone
                    // else, or time has passed) — say so and show fresh slots.
                    Flash = "That slot was just taken — please pick another time below.";
                }
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnPostBookAsync(string slug)
    {
        if (!await LoadTenantAsync(slug))
        {
            TenantMissing = true;
            Response.StatusCode = StatusCodes.Status404NotFound;
            return Page();
        }

        var name = FormName?.Trim() ?? "";
        var contact = FormContact?.Trim() ?? "";
        if (name.Length == 0 || contact.Length == 0 ||
            !DateTimeOffset.TryParse(FormSlot, out var startUtc))
        {
            // Re-render the confirm step with the error.
            ConfirmService = AllServices.FirstOrDefault(x => x.Id == FormServiceId);
            ConfirmStaff = AllStaff.FirstOrDefault(x => x.Id == FormStaffId);
            if (ConfirmService is not null && ConfirmStaff is not null &&
                DateTimeOffset.TryParse(FormSlot, out var parsed))
            {
                var tz = TenantTz();
                var local = TimeZoneInfo.ConvertTime(parsed, tz);
                ConfirmSlot = new SlotInfo(parsed, local);
                FormError = name.Length == 0 || contact.Length == 0
                    ? "Please enter your name and a phone number or email so the business can reach you."
                    : "That time is no longer available — please pick another slot.";
            }
            else
            {
                Flash = "That time is no longer available — please pick another slot.";
                return RedirectToPage(new { slug });
            }
            return Page();
        }

        var result = await _bookings.CreateAsync(
            FormServiceId, FormStaffId, name, contact, startUtc);

        switch (result.Outcome)
        {
            case BookingOutcome.Success:
                return RedirectToPage(new { slug, booked = result.Booking!.Id });
            case BookingOutcome.SlotTaken:
                Flash = "That slot was just taken — please pick another time below.";
                break;
            case BookingOutcome.InPast:
            case BookingOutcome.OutsideHours:
                Flash = "That time is no longer available — please pick another slot.";
                break;
            default:
                Flash = "That booking couldn't be made — please pick another slot.";
                break;
        }

        return RedirectToPage(new
        {
            slug,
            serviceId = FormServiceId,
            staffId = FormStaffId,
            date = FormDate,
        });
    }

    private async Task<bool> LoadTenantAsync(string slug)
    {
        // Slug resolution deliberately bypasses the query filters (no tenant
        // is selected yet); everything after this is tenant-scoped.
        var tenant = await _db.Tenants.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Slug == slug);
        if (tenant is null)
            return false;

        Tenant = tenant;
        _tenantCtx.TenantId = tenant.Id;

        AllServices = await _db.Services.OrderBy(s => s.Name).ToListAsync();
        AllStaff = await _db.StaffMembers.OrderBy(s => s.Name).ToListAsync();
        Services = AllServices.Where(s => s.IsActive).ToList();
        Staff = AllStaff.Where(s => s.IsActive).ToList();
        return true;
    }

    /// <summary>Converts a UTC instant to the tenant's local time (views).</summary>
    public DateTimeOffset Local(DateTimeOffset utc) =>
        TimeZoneInfo.ConvertTime(utc, TenantTz());

    private TimeZoneInfo TenantTz() =>
        TimeZoneInfo.FindSystemTimeZoneById(Tenant.TimeZoneId);

    private DateOnly TenantToday(TimeZoneInfo tz) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_time.GetUtcNow(), tz).DateTime);
}

internal static class DateOnlyExtensions
{
    public static DateOnly Clamp(this DateOnly value, DateOnly min, DateOnly max) =>
        value < min ? min : value > max ? max : value;
}
