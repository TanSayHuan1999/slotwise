using Microsoft.EntityFrameworkCore;
using Npgsql;
using Slotwise.Data;
using Slotwise.Domain;
using Slotwise.Services;

namespace Slotwise.Tests;

/// <summary>
/// Creates a throwaway database on the dev PostgreSQL server, applies the
/// real EF migrations (including the exclusion-constraint migration), and
/// drops the database afterwards. Each test run gets its own database, so
/// runs never collide.
/// </summary>
public class TestDbFixture : IAsyncLifetime
{
    // The admin connection points at a maintenance database (postgres) with
    // a user allowed to create/drop databases. CI — and any normal machine —
    // supplies it via SLOTWISE_TEST_ADMIN_CONNECTION, a full Npgsql
    // connection string over TCP. Locally it falls back to this sandbox's
    // PostgreSQL Unix socket: the sandbox transparently redirects outbound
    // TCP connects from .NET processes to an egress proxy, which breaks
    // Npgsql-over-TCP (and the old vstest channel); Unix sockets are
    // unaffected.
    private const string SocketHost = "/home/hatch/pg16/run";

    private static readonly string AdminBase =
        Environment.GetEnvironmentVariable("SLOTWISE_TEST_ADMIN_CONNECTION")
        ?? $"Host={SocketHost};Port=5434;Database=postgres;Username=settle";

    private static string AdminConnectionString => AdminBase;

    public string DatabaseName { get; } = $"slotwise_test_{Guid.NewGuid():N}";

    public string ConnectionString =>
        new NpgsqlConnectionStringBuilder(AdminBase) { Database = DatabaseName }
            .ConnectionString;

    public async ValueTask InitializeAsync()
    {
        await using (var admin = new NpgsqlConnection(AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{DatabaseName}\"", admin);
            await cmd.ExecuteNonQueryAsync();
        }

        var scope = NewScope(null);
        await scope.Db.Database.MigrateAsync();
        scope.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(AdminConnectionString);
        await admin.OpenAsync();
        await using (var kick = new NpgsqlCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity " +
            $"WHERE datname = '{DatabaseName}' AND pid <> pg_backend_pid()", admin))
        {
            await kick.ExecuteNonQueryAsync();
        }
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{DatabaseName}\"", admin);
        await drop.ExecuteNonQueryAsync();
    }

    /// <summary>A DbContext + tenant context + booking service wired together
    /// the same way DI wires them in the app.</summary>
    public TestScope NewScope(Guid? tenantId)
    {
        var tenant = new TenantContext { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<SlotwiseDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        var db = new SlotwiseDbContext(options, tenant);
        return new TestScope(db, tenant, new BookingService(db, tenant, TimeProvider.System));
    }

    /// <summary>
    /// Seeds a tenant (Asia/Singapore) with one service, one staff member and
    /// working hours on the target date's weekday. Every test gets its own
    /// tenant, so tests sharing one database stay independent.
    /// </summary>
    public async Task<Basics> SeedBasicsAsync(
        DateOnly date, int durationMinutes = 60,
        TimeOnly? opens = null, TimeOnly? closes = null)
    {
        opens ??= new TimeOnly(9, 0);
        closes ??= new TimeOnly(18, 0);

        var tenantId = Guid.NewGuid();
        using (var scope = NewScope(null))
        {
            scope.Db.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = "Test Tenant",
                Slug = $"t-{tenantId:N}"[..14],
                TimeZoneId = "Asia/Singapore",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await scope.Db.SaveChangesAsync();
        }

        var serviceId = Guid.NewGuid();
        var staffId = Guid.NewGuid();
        using (var scope = NewScope(tenantId))
        {
            scope.Db.Services.Add(new Service
            {
                Id = serviceId, TenantId = tenantId, Name = "Appointment",
                DurationMinutes = durationMinutes, PriceCents = 5000, IsActive = true,
            });
            scope.Db.StaffMembers.Add(new StaffMember
            {
                Id = staffId, TenantId = tenantId, Name = "Staff One", IsActive = true,
            });
            scope.Db.WorkingHours.Add(new WorkingHours
            {
                Id = Guid.NewGuid(), TenantId = tenantId, StaffMemberId = staffId,
                DayOfWeek = date.DayOfWeek, StartTime = opens.Value, EndTime = closes.Value,
            });
            await scope.Db.SaveChangesAsync();
        }

        return new Basics(tenantId, serviceId, staffId);
    }

    /// <summary>Inserts a booking directly, bypassing BookingService
    /// validation (but NOT the database exclusion constraint).</summary>
    public async Task<Guid> InsertBookingAsync(
        Guid tenantId, Guid serviceId, Guid staffId,
        DateTimeOffset startUtc, DateTimeOffset endUtc,
        BookingStatus status = BookingStatus.Confirmed,
        DateTimeOffset? reminderSentAt = null)
    {
        var id = Guid.NewGuid();
        using var scope = NewScope(tenantId);
        scope.Db.Bookings.Add(new Booking
        {
            Id = id,
            TenantId = tenantId,
            ServiceId = serviceId,
            StaffMemberId = staffId,
            CustomerName = "Direct Insert",
            CustomerContact = "direct@example.com",
            StartUtc = startUtc,
            EndUtc = endUtc,
            Status = status,
            ReminderSentAt = reminderSentAt,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await scope.Db.SaveChangesAsync();
        return id;
    }
}

public sealed record Basics(Guid TenantId, Guid ServiceId, Guid StaffId);

public sealed class TestScope : IDisposable
{
    public TestScope(SlotwiseDbContext db, TenantContext tenant, BookingService bookings)
    {
        Db = db;
        Tenant = tenant;
        Bookings = bookings;
    }

    public SlotwiseDbContext Db { get; }
    public TenantContext Tenant { get; }
    public BookingService Bookings { get; }

    public void Dispose() => Db.Dispose();
}
