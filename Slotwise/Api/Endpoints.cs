using Microsoft.EntityFrameworkCore;
using Slotwise.Data;
using Slotwise.Domain;
using Slotwise.Services;

namespace Slotwise.Api;

public static class Endpoints
{
    public static void MapSlotwiseApi(this WebApplication app)
    {
        app.MapGet("/healthz", async (SlotwiseDbContext db) =>
            await db.Database.CanConnectAsync()
                ? Results.Ok(new { status = "ok" })
                : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

        // --- Tenants -------------------------------------------------------
        app.MapPost("/api/tenants", async (
            CreateTenantRequest req, SlotwiseDbContext db, TimeProvider time) =>
        {
            var tenant = new Tenant
            {
                Id = Guid.NewGuid(),
                Name = req.Name,
                Slug = req.Slug,
                TimeZoneId = req.TimeZoneId ?? "Asia/Singapore",
                CreatedAt = time.GetUtcNow(),
            };
            db.Tenants.Add(tenant);
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                return Results.Conflict(new { error = "slug already taken" });
            }
            return Results.Created($"/api/tenants/{tenant.Slug}", TenantDto.From(tenant));
        });

        app.MapPost("/api/tenants/{slug}/services", async (
            string slug, CreateServiceRequest req,
            SlotwiseDbContext db, ITenantContext tenantCtx) =>
        {
            var tenant = await ResolveTenantAsync(db, tenantCtx, slug);
            if (tenant is null) return Results.NotFound();

            var service = new Service
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                Name = req.Name,
                DurationMinutes = req.DurationMinutes,
                PriceCents = req.PriceCents,
                IsActive = true,
            };
            db.Services.Add(service);
            await db.SaveChangesAsync();
            return Results.Created($"/api/tenants/{slug}/services/{service.Id}", ServiceDto.From(service));
        });

        app.MapPost("/api/tenants/{slug}/staff", async (
            string slug, CreateStaffRequest req,
            SlotwiseDbContext db, ITenantContext tenantCtx) =>
        {
            var tenant = await ResolveTenantAsync(db, tenantCtx, slug);
            if (tenant is null) return Results.NotFound();

            var staff = new StaffMember
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                Name = req.Name,
                IsActive = true,
            };
            db.StaffMembers.Add(staff);
            await db.SaveChangesAsync();
            return Results.Created($"/api/tenants/{slug}/staff/{staff.Id}", StaffDto.From(staff));
        });

        // Replace-all weekly hours for one staff member.
        app.MapPut("/api/tenants/{slug}/staff/{staffId:guid}/hours", async (
            string slug, Guid staffId, SetHoursRequest req,
            SlotwiseDbContext db, ITenantContext tenantCtx) =>
        {
            var tenant = await ResolveTenantAsync(db, tenantCtx, slug);
            if (tenant is null) return Results.NotFound();

            var staff = await db.StaffMembers.FirstOrDefaultAsync(s => s.Id == staffId);
            if (staff is null) return Results.NotFound();

            var existing = await db.WorkingHours
                .Where(w => w.StaffMemberId == staffId)
                .ToListAsync();
            db.WorkingHours.RemoveRange(existing);

            foreach (var h in req.Hours)
            {
                if (!TimeOnly.TryParse(h.Start, out var start) || !TimeOnly.TryParse(h.End, out var end))
                    return Results.UnprocessableEntity(new { error = $"bad time format: '{h.Start}'-'{h.End}' (want HH:mm)" });
                if (end <= start)
                    return Results.UnprocessableEntity(new { error = "end must be after start" });

                db.WorkingHours.Add(new WorkingHours
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenant.Id,
                    StaffMemberId = staffId,
                    DayOfWeek = (DayOfWeek)h.DayOfWeek,
                    StartTime = start,
                    EndTime = end,
                });
            }
            await db.SaveChangesAsync();
            return Results.Ok(new { staffId, hours = req.Hours.Count });
        });

        // --- Public booking ------------------------------------------------
        app.MapGet("/api/book/{slug}/slots", async (
            string slug, Guid serviceId, Guid staffId, string date,
            SlotwiseDbContext db, ITenantContext tenantCtx, BookingService bookings) =>
        {
            var tenant = await ResolveTenantAsync(db, tenantCtx, slug);
            if (tenant is null) return Results.NotFound();
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", out var day))
                return Results.UnprocessableEntity(new { error = "date must be yyyy-MM-dd" });

            var result = await bookings.GetSlotsAsync(serviceId, staffId, day);
            if (!result.Found) return Results.NotFound();

            return Results.Ok(result.Slots.Select(s => new SlotDto(
                s.StartUtc,
                s.LocalStart.ToString("yyyy-MM-dd HH:mm"))));
        });

        app.MapPost("/api/book/{slug}", async (
            string slug, CreateBookingRequest req,
            SlotwiseDbContext db, ITenantContext tenantCtx, BookingService bookings) =>
        {
            var tenant = await ResolveTenantAsync(db, tenantCtx, slug);
            if (tenant is null) return Results.NotFound();

            var result = await bookings.CreateAsync(
                req.ServiceId, req.StaffId, req.CustomerName, req.CustomerContact, req.StartUtc);

            return result.Outcome switch
            {
                BookingOutcome.Success => Results.Created(
                    $"/api/bookings/{result.Booking!.Id}", BookingDto.From(result.Booking)),
                BookingOutcome.SlotTaken => Results.Conflict(
                    new { error = "slot taken" }),
                BookingOutcome.OutsideHours => Results.UnprocessableEntity(
                    new { error = "outside working hours" }),
                BookingOutcome.InPast => Results.UnprocessableEntity(
                    new { error = "cannot book in the past" }),
                _ => Results.NotFound(),
            };
        });

        app.MapPost("/api/tenants/{slug}/bookings/{bookingId:guid}/cancel", async (
            string slug, Guid bookingId,
            SlotwiseDbContext db, ITenantContext tenantCtx, BookingService bookings) =>
        {
            var tenant = await ResolveTenantAsync(db, tenantCtx, slug);
            if (tenant is null) return Results.NotFound();

            return await bookings.CancelAsync(bookingId) switch
            {
                CancelOutcome.Cancelled => Results.Ok(new { id = bookingId, status = "Cancelled" }),
                CancelOutcome.NotCancellable => Results.Conflict(new { error = "booking is not cancellable" }),
                _ => Results.NotFound(),
            };
        });

        // --- Dev seed --------------------------------------------------------
        app.MapPost("/api/dev/seed", async (
            SlotwiseDbContext db, ITenantContext tenantCtx, TimeProvider time) =>
        {
            const string slug = "lakeside-dental";
            var existing = await db.Tenants.IgnoreQueryFilters()
                .FirstOrDefaultAsync(t => t.Slug == slug);
            if (existing is not null)
            {
                tenantCtx.TenantId = existing.Id;
                return Results.Ok(new
                {
                    created = false,
                    tenant = TenantDto.From(existing),
                    services = await db.Services.Select(s => ServiceDto.From(s)).ToListAsync(),
                    staff = await db.StaffMembers.Select(s => StaffDto.From(s)).ToListAsync(),
                });
            }

            var tenant = new Tenant
            {
                Id = Guid.NewGuid(),
                Name = "Lakeside Dental",
                Slug = slug,
                TimeZoneId = "Asia/Singapore",
                CreatedAt = time.GetUtcNow(),
            };
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();
            tenantCtx.TenantId = tenant.Id;

            var checkup = new Service { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Checkup", DurationMinutes = 30, PriceCents = 5000 };
            var cleaning = new Service { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Cleaning", DurationMinutes = 60, PriceCents = 8000 };
            db.Services.AddRange(checkup, cleaning);

            var chen = new StaffMember { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Dr. Chen" };
            var patel = new StaffMember { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Dr. Patel" };
            db.StaffMembers.AddRange(chen, patel);

            foreach (var staff in new[] { chen, patel })
            foreach (var day in new[]
            {
                DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday,
            })
            {
                db.WorkingHours.Add(new WorkingHours
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenant.Id,
                    StaffMemberId = staff.Id,
                    DayOfWeek = day,
                    StartTime = new TimeOnly(9, 0),
                    EndTime = new TimeOnly(18, 0),
                });
            }
            await db.SaveChangesAsync();

            return Results.Created("/api/tenants/lakeside-dental", new
            {
                created = true,
                tenant = TenantDto.From(tenant),
                services = new[] { ServiceDto.From(checkup), ServiceDto.From(cleaning) },
                staff = new[] { StaffDto.From(chen), StaffDto.From(patel) },
            });
        });
    }

    /// <summary>
    /// Resolves a tenant by slug and scopes the current unit of work to it.
    /// The lookup itself bypasses query filters (no tenant is selected yet);
    /// everything after this call is tenant-scoped.
    /// </summary>
    private static async Task<Tenant?> ResolveTenantAsync(
        SlotwiseDbContext db, ITenantContext tenantCtx, string slug)
    {
        var tenant = await db.Tenants.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Slug == slug);
        if (tenant is not null)
            tenantCtx.TenantId = tenant.Id;
        return tenant;
    }
}

// --- DTOs -------------------------------------------------------------------

public sealed record CreateTenantRequest(string Name, string Slug, string? TimeZoneId);
public sealed record CreateServiceRequest(string Name, int DurationMinutes, int PriceCents);
public sealed record CreateStaffRequest(string Name);
public sealed record HourEntry(int DayOfWeek, string Start, string End);
public sealed record SetHoursRequest(List<HourEntry> Hours);
public sealed record CreateBookingRequest(
    Guid ServiceId, Guid StaffId, string CustomerName, string CustomerContact, DateTimeOffset StartUtc);

public sealed record TenantDto(Guid Id, string Name, string Slug, string TimeZoneId)
{
    public static TenantDto From(Tenant t) => new(t.Id, t.Name, t.Slug, t.TimeZoneId);
}

public sealed record ServiceDto(Guid Id, string Name, int DurationMinutes, int PriceCents, bool IsActive)
{
    public static ServiceDto From(Service s) => new(s.Id, s.Name, s.DurationMinutes, s.PriceCents, s.IsActive);
}

public sealed record StaffDto(Guid Id, string Name, bool IsActive)
{
    public static StaffDto From(StaffMember s) => new(s.Id, s.Name, s.IsActive);
}

public sealed record SlotDto(DateTimeOffset StartUtc, string LocalStart);

public sealed record BookingDto(
    Guid Id, Guid ServiceId, Guid StaffId, string CustomerName,
    DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Status)
{
    public static BookingDto From(Booking b) => new(
        b.Id, b.ServiceId, b.StaffMemberId, b.CustomerName, b.StartUtc, b.EndUtc, b.Status.ToString());
}
