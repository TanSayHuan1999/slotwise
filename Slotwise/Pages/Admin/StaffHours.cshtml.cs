using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slotwise.Data;
using Slotwise.Domain;

namespace Slotwise.Pages.Admin;

/// <summary>
/// Weekly working-hours editor for one staff member. Saving replaces the
/// member's whole week (the same replace-all semantics as the stage-1 API):
/// a day with "Open" unchecked simply has no window, so no slots are
/// offered that day.
/// </summary>
public class StaffHoursModel : AdminPageModel
{
    public StaffHoursModel(SlotwiseDbContext db, TimeProvider time) : base(db, time)
    {
    }

    public StaffMember Staff { get; private set; } = null!;
    public string? FormError { get; private set; }

    [BindProperty]
    public List<HourRow> Rows { get; set; } = [];

    [TempData]
    public string? Flash { get; set; }

    public sealed class HourRow
    {
        public DayOfWeek Day { get; set; }
        public bool Enabled { get; set; }
        public string Start { get; set; } = "09:00";
        public string End { get; set; } = "18:00";
    }

    private static readonly DayOfWeek[] OrderedDays =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
        DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday,
        DayOfWeek.Sunday,
    ];

    public async Task<IActionResult> OnGetAsync(Guid staffId)
    {
        var staff = await Db.StaffMembers.FirstOrDefaultAsync(s => s.Id == staffId);
        if (staff is null)
            return NotFound();
        Staff = staff;

        var hours = await Db.WorkingHours
            .Where(w => w.StaffMemberId == staffId)
            .ToListAsync();

        Rows = OrderedDays.Select(day =>
        {
            var h = hours.FirstOrDefault(x => x.DayOfWeek == day);
            return new HourRow
            {
                Day = day,
                Enabled = h is not null,
                Start = h?.StartTime.ToString("HH:mm") ?? "09:00",
                End = h?.EndTime.ToString("HH:mm") ?? "18:00",
            };
        }).ToList();

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid staffId)
    {
        var staff = await Db.StaffMembers.FirstOrDefaultAsync(s => s.Id == staffId);
        if (staff is null)
            return NotFound();
        Staff = staff;

        foreach (var row in Rows.Where(r => r.Enabled))
        {
            if (!TimeOnly.TryParse(row.Start, out var start) ||
                !TimeOnly.TryParse(row.End, out var end) || end <= start)
            {
                FormError = $"Invalid hours for {row.Day}: closing time must be after opening time.";
                return Page();
            }
        }

        var tenant = await TenantAsync();
        var existing = await Db.WorkingHours
            .Where(w => w.StaffMemberId == staffId)
            .ToListAsync();
        Db.WorkingHours.RemoveRange(existing);

        foreach (var row in Rows.Where(r => r.Enabled))
        {
            Db.WorkingHours.Add(new WorkingHours
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                StaffMemberId = staffId,
                DayOfWeek = row.Day,
                StartTime = TimeOnly.Parse(row.Start),
                EndTime = TimeOnly.Parse(row.End),
            });
        }

        await Db.SaveChangesAsync();
        Flash = $"Working hours saved for {staff.Name}.";
        return RedirectToPage(new { staffId });
    }
}
