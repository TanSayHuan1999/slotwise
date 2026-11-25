using Microsoft.EntityFrameworkCore;
using Slotwise.Data;

namespace Slotwise.Services;

/// <summary>
/// Background worker that "sends" appointment reminders. Every
/// Reminders:IntervalMinutes it scans (across all tenants — the stage-1
/// reminder query deliberately bypasses the tenant filters, because a
/// background job has no current tenant) for Confirmed bookings starting
/// within Reminders:WindowHours that have not been reminded yet, stamps
/// ReminderSentAt on them, and logs one line per reminder.
///
/// v1 sends no real email/SMS: the stamp is the record, and it surfaces in
/// the admin UI as the "reminders sent" count and a badge on each booking.
///
/// ScanOnceAsync is public so tests (and operators) can trigger a single
/// pass without waiting for the timer.
/// </summary>
public class ReminderService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IConfiguration _config;
    private readonly ILogger<ReminderService> _logger;
    private readonly TimeProvider _time;

    public ReminderService(
        IServiceScopeFactory scopes, IConfiguration config,
        ILogger<ReminderService> logger, TimeProvider time)
    {
        _scopes = scopes;
        _config = config;
        _logger = logger;
        _time = time;
    }

    public int IntervalMinutes => Math.Max(1, _config.GetValue("Reminders:IntervalMinutes", 5));
    public int WindowHours => Math.Max(1, _config.GetValue("Reminders:WindowHours", 2));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Reminder service started: scanning every {Interval} min for bookings within {Window} h.",
            IntervalMinutes, WindowHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed scan must not kill the worker; log and retry at
                // the next interval.
                _logger.LogError(ex, "Reminder scan failed; will retry next interval.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(IntervalMinutes), _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One scan pass. Returns how many reminders were marked.</summary>
    public async Task<int> ScanOnceAsync(CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SlotwiseDbContext>();
        var bookingService = scope.ServiceProvider.GetRequiredService<BookingService>();

        var due = await bookingService.GetUpcomingRemindersAsync(WindowHours, ct);
        if (due.Count == 0)
            return 0;

        var tenants = await db.Tenants.IgnoreQueryFilters()
            .ToDictionaryAsync(t => t.Id, ct);
        var serviceNames = await db.Services.IgnoreQueryFilters()
            .ToDictionaryAsync(s => s.Id, s => s.Name, ct);

        var now = _time.GetUtcNow();
        foreach (var booking in due)
        {
            booking.ReminderSentAt = now;

            var tenant = tenants[booking.TenantId];
            var tz = TimeZoneInfo.FindSystemTimeZoneById(tenant.TimeZoneId);
            var local = TimeZoneInfo.ConvertTime(booking.StartUtc, tz);
            _logger.LogInformation(
                "Reminder sent: {Customer} ({Contact}) at {Tenant} — {Service}, {Date} {Time}.",
                booking.CustomerName, booking.CustomerContact, tenant.Name,
                serviceNames.GetValueOrDefault(booking.ServiceId, "appointment"),
                local.ToString("yyyy-MM-dd"), local.ToString("h:mm tt"));
        }

        await db.SaveChangesAsync(ct);
        return due.Count;
    }
}
