using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Slotwise.Data;
using Slotwise.Domain;
using Slotwise.Web;

namespace Slotwise.Pages.Admin;

public class LoginModel : PageModel
{
    private readonly SlotwiseDbContext _db;
    private readonly IPasswordHasher<TenantAdmin> _hasher;

    public LoginModel(SlotwiseDbContext db, IPasswordHasher<TenantAdmin> hasher)
    {
        _db = db;
        _hasher = hasher;
    }

    [BindProperty] public string Email { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    public string? Error { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl)
    {
        var email = Email.Trim().ToLowerInvariant();

        // The login lookup is the one place that must search across
        // tenants (nobody is signed in yet, so there is no tenant context):
        // IgnoreQueryFilters, matched on the platform-unique email.
        var admin = await _db.TenantAdmins.IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.Email.ToLower() == email);

        if (admin is null ||
            _hasher.VerifyHashedPassword(admin, admin.PasswordHash, Password)
                == PasswordVerificationResult.Failed)
        {
            Error = "Invalid email or password.";
            return Page();
        }

        var tenant = await _db.Tenants.IgnoreQueryFilters()
            .FirstAsync(t => t.Id == admin.TenantId);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, admin.Id.ToString()),
            new(ClaimTypes.Email, admin.Email),
            new(ClaimTypes.Name, admin.Email),
            new(SlotwiseClaims.TenantId, tenant.Id.ToString()),
            new(SlotwiseClaims.TenantSlug, tenant.Slug),
            new(SlotwiseClaims.TenantName, tenant.Name),
        };
        var identity = new ClaimsIdentity(
            claims, CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity));

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return LocalRedirect(returnUrl);
        return RedirectToPage("/Admin/Index");
    }
}
