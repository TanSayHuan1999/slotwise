using Microsoft.EntityFrameworkCore;
using Slotwise.Api;
using Slotwise.Data;
using Slotwise.Services;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Slotwise")
    ?? throw new InvalidOperationException("ConnectionStrings:Slotwise is not configured.");

builder.Services.AddScoped<ITenantContext, TenantContext>();
builder.Services.AddDbContext<SlotwiseDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddScoped<BookingService>();
builder.Services.AddSingleton(TimeProvider.System);

var app = builder.Build();

// Apply migrations on startup (dev/showcase convenience; the schema —
// including the no-overlap exclusion constraint — lives in migrations).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SlotwiseDbContext>();
    await db.Database.MigrateAsync();
}

app.MapSlotwiseApi();
app.Run();

public partial class Program { }
