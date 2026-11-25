using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slotwise.Data;
using Slotwise.Domain;
using Slotwise.Services;

namespace Slotwise.Pages.Admin;

public class BookingsModel : AdminPageModel
{
    private readonly BookingService _bookingService;

    public BookingsModel(SlotwiseDbContext db, TimeProvider time, BookingService bookingService)
        : base(db, time)
    {
        _bookingService = bookingService;
    }

    /// <summary>Date filter (yyyy-MM-dd, tenant-local). Null on first visit
    /// defaults to today; an explicitly empty value means "all dates".</summary>
    [BindProperty(SupportsGet = true)]
    public string? Date { get; set; }

    /// <summary>Status filter (BookingStatus name); empty = all statuses.</summary>
    [BindProperty(SupportsGet = true)]
    public string? Status { get; set; }

    public Tenant Tenant { get; private set; } = null!;
    public TimeZoneInfo Tz { get; private set; } = null!;
    public IReadOnlyList<Booking> Bookings { get; private set; } = [];
    public Dictionary<Guid, string> ServiceNames { get; private set; } = new();
    public Dictionary<Guid, string> StaffNames { get; private set; } = new();

    [TempData]
    public string? Flash { get; set; }

    /// <summary>Converts a UTC instant to tenant-local time (for views).</summary>
    public DateTimeOffset Local(DateTimeOffset utc) => ToLocal(utc, Tz);

    public async Task OnGetAsync()
    {
        Tenant = await TenantAsync();
        Tz = await TzAsync();

        Date ??= DateOnly.FromDateTime(ToLocal(Time.GetUtcNow(), Tz).DateTime)
            .ToString("yyyy-MM-dd");

        var query = Db.Bookings.AsQueryable();

        if (DateOnly.TryParse(Date, out var day))
        {
            var (start, end) = LocalDayRange(day, Tz);
            query = query.Where(b => b.StartUtc >= start && b.StartUtc < end);
        }
        if (Enum.TryParse<BookingStatus>(Status, ignoreCase: true, out var status))
            query = query.Where(b => b.Status == status);

        Bookings = await query
            .OrderBy(b => b.StartUtc)
            .Take(200)
            .ToListAsync();

        (ServiceNames, StaffNames) = await NameMapsAsync();
    }

    public async Task<IActionResult> OnPostCancelAsync(Guid bookingId)
    {
        // Cancellation goes through BookingService so the state machine and
        // the "cancel frees the slot" rule live in exactly one place.
        var outcome = await _bookingService.CancelAsync(bookingId);
        Flash = outcome switch
        {
            CancelOutcome.Cancelled => "Booking cancelled — the slot is available again.",
            CancelOutcome.NotCancellable => "That booking can no longer be cancelled.",
            _ => "Booking not found.",
        };
        return RedirectToPage(new { Date, Status });
    }

    public async Task<IActionResult> OnPostCompleteAsync(Guid bookingId) =>
        await TransitionAsync(bookingId, BookingStatus.Completed, "Booking marked completed.");

    public async Task<IActionResult> OnPostNoShowAsync(Guid bookingId) =>
        await TransitionAsync(bookingId, BookingStatus.NoShow, "Booking marked as no-show.");

    /// <summary>
    /// The booking state machine: only a Confirmed booking can move to
    /// Completed / NoShow (or be cancelled, via BookingService). Anything
    /// else is a no-op with an explanatory flash.
    /// </summary>
    private async Task<IActionResult> TransitionAsync(
        Guid bookingId, BookingStatus target, string successFlash)
    {
        var booking = await Db.Bookings.FirstOrDefaultAsync(b => b.Id == bookingId);
        if (booking is null)
        {
            Flash = "Booking not found.";
        }
        else if (booking.Status != BookingStatus.Confirmed)
        {
            Flash = $"A {booking.Status} booking can't become {target}.";
        }
        else
        {
            booking.Status = target;
            await Db.SaveChangesAsync();
            Flash = successFlash;
        }
        return RedirectToPage(new { Date, Status });
    }
}
