using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slotwise.Data;
using Slotwise.Domain;

namespace Slotwise.Pages.Admin;

public class ServicesModel : AdminPageModel
{
    public ServicesModel(SlotwiseDbContext db, TimeProvider time) : base(db, time)
    {
    }

    public IReadOnlyList<Service> Services { get; private set; } = [];
    public string? FormError { get; private set; }

    [BindProperty] public Guid? FormId { get; set; }
    [BindProperty] public string FormName { get; set; } = "";
    [BindProperty] public int FormDuration { get; set; } = 30;
    [BindProperty] public decimal FormPrice { get; set; }
    [BindProperty] public bool FormActive { get; set; } = true;

    public async Task OnGetAsync(Guid? edit)
    {
        await LoadAsync();
        if (edit is { } id && Services.FirstOrDefault(s => s.Id == id) is { } service)
        {
            FormId = service.Id;
            FormName = service.Name;
            FormDuration = service.DurationMinutes;
            FormPrice = service.PriceCents / 100m;
            FormActive = service.IsActive;
        }
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        if (string.IsNullOrWhiteSpace(FormName) || FormDuration is < 5 or > 480 || FormPrice < 0)
        {
            FormError = "Name is required; duration must be 5–480 minutes; price can't be negative.";
            await LoadAsync();
            return Page();
        }

        var priceCents = (int)Math.Round(FormPrice * 100);
        if (FormId is { } id)
        {
            var service = await Db.Services.FirstOrDefaultAsync(s => s.Id == id);
            if (service is null)
                return RedirectToPage();
            service.Name = FormName.Trim();
            service.DurationMinutes = FormDuration;
            service.PriceCents = priceCents;
            service.IsActive = FormActive;
        }
        else
        {
            var tenant = await TenantAsync();
            Db.Services.Add(new Service
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                Name = FormName.Trim(),
                DurationMinutes = FormDuration,
                PriceCents = priceCents,
                IsActive = FormActive,
            });
        }

        await Db.SaveChangesAsync();
        return RedirectToPage();
    }

    private async Task LoadAsync() =>
        Services = await Db.Services.OrderBy(s => s.Name).ToListAsync();
}
