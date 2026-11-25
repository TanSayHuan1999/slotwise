using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Slotwise.Data;
using Slotwise.Domain;
using Slotwise.Services;

namespace Slotwise.Pages;

public class IndexModel : PageModel
{
    private readonly SlotwiseDbContext _db;

    public IndexModel(SlotwiseDbContext db) => _db = db;

    public Tenant? DemoTenant { get; private set; }

    public async Task OnGetAsync()
    {
        DemoTenant = await _db.Tenants.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Slug == DemoSeeder.DemoSlug);
    }
}
