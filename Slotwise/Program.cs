using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Slotwise.Api;
using Slotwise.Data;
using Slotwise.Domain;
using Slotwise.Services;
using Slotwise.Web;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Slotwise")
    ?? builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Slotwise is not configured.");

builder.Services.AddScoped<ITenantContext, TenantContext>();
builder.Services.AddDbContext<SlotwiseDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddScoped<BookingService>();
builder.Services.AddScoped<DemoSeeder>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IPasswordHasher<TenantAdmin>, PasswordHasher<TenantAdmin>>();

// --- Admin authentication (cookie) --------------------------------------
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/admin/login";
        options.LogoutPath = "/admin/logout";
        options.AccessDeniedPath = "/admin/login";
        options.Cookie.Name = "Slotwise.Admin";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.AddRazorPages(options =>
{
    // Everything under /admin requires a logged-in admin except the login
    // and logout pages themselves.
    options.Conventions.AuthorizeFolder("/Admin");
    options.Conventions.AllowAnonymousToPage("/Admin/Login");
    options.Conventions.AllowAnonymousToPage("/Admin/Logout");
});

builder.Services.AddHostedService<ReminderService>();

var app = builder.Build();

// Stage 3 puts the app behind Caddy under /slotwise; PathBase (env
// `PathBase`) makes every route, cookie and tag-helper URL honour it.
var pathBase = app.Configuration["PathBase"];
if (!string.IsNullOrWhiteSpace(pathBase))
    app.UsePathBase(pathBase);

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<TenantClaimMiddleware>();

// Apply migrations on startup (dev/showcase convenience; the schema —
// including the no-overlap exclusion constraint — lives in migrations),
// then seed the demo tenant if the database is completely empty.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SlotwiseDbContext>();
    await db.Database.MigrateAsync();

    if (!await db.Tenants.IgnoreQueryFilters().AnyAsync())
    {
        var seeder = scope.ServiceProvider.GetRequiredService<DemoSeeder>();
        await seeder.SeedAsync();
    }
}

app.MapRazorPages();
app.MapSlotwiseApi();
app.Run();

public partial class Program { }
