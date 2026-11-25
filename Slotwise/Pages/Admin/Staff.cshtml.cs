using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slotwise.Data;
using Slotwise.Domain;

namespace Slotwise.Pages.Admin;

public class StaffModel : AdminPageModel
{
    public StaffModel(SlotwiseDbContext db, TimeProvider time) : base(db, time)
    {
    }

    public IReadOnlyList<StaffMember> Staff { get; private set; } = [];
    public string? FormError { get; private set; }

    [BindProperty] public Guid? FormId { get; set; }
    [BindProperty] public string FormName { get; set; } = "";
    [BindProperty] public bool FormActive { get; set; } = true;

    public async Task OnGetAsync(Guid? edit)
    {
        await LoadAsync();
        if (edit is { } id && Staff.FirstOrDefault(s => s.Id == id) is { } member)
        {
            FormId = member.Id;
            FormName = member.Name;
            FormActive = member.IsActive;
        }
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (string.IsNullOrWhiteSpace(FormName))
        {
            FormError = "Name is required.";
            await LoadAsync();
            return Page();
        }

        if (FormId is { } id)
        {
            var member = await Db.StaffMembers.FirstOrDefaultAsync(s => s.Id == id);
            if (member is null)
                return RedirectToPage();
            member.Name = FormName.Trim();
            member.IsActive = FormActive;
        }
        else
        {
            var tenant = await TenantAsync();
            Db.StaffMembers.Add(new StaffMember
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                Name = FormName.Trim(),
                IsActive = FormActive,
            });
        }

        await Db.SaveChangesAsync();
        return RedirectToPage();
    }

    private async Task LoadAsync() =>
        Staff = await Db.StaffMembers.OrderBy(s => s.Name).ToListAsync();
}
