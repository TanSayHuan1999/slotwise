using Microsoft.EntityFrameworkCore;
using Npgsql;
using Slotwise.Domain;
using Slotwise.Services;

namespace Slotwise.Tests;

public class BookingTests : IClassFixture<TestDbFixture>
{
    private static readonly TimeZoneInfo SgTz =
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Singapore");

    private readonly TestDbFixture _fx;

    public BookingTests(TestDbFixture fx) => _fx = fx;

    /// <summary>A date safely in the future; tests set working hours for
    /// whatever weekday it lands on.</summary>
    private static DateOnly FutureDate => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(7);

    private static DateTimeOffset LocalToUtc(DateOnly date, int hour, int minute = 0) =>
        new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(new TimeOnly(hour, minute)), SgTz));

    // 1 ------------------------------------------------------------------
    [Fact]
    public async Task SlotGeneration_ExcludesOverlappingCandidates()
    {
        var date = FutureDate;
        var basics = await _fx.SeedBasicsAsync(date, durationMinutes: 60,
            opens: new TimeOnly(9, 0), closes: new TimeOnly(12, 0));

        // Existing booking 10:00–11:00 local.
        await _fx.InsertBookingAsync(basics.TenantId, basics.ServiceId, basics.StaffId,
            LocalToUtc(date, 10), LocalToUtc(date, 11));

        using var scope = _fx.NewScope(basics.TenantId);
        var result = await scope.Bookings.GetSlotsAsync(basics.ServiceId, basics.StaffId, date);

        Assert.True(result.Found);
        var localStarts = result.Slots.Select(s => s.LocalStart.TimeOfDay).ToList();
        // Candidates step 15 min from 09:00; [start, start+60m) must fit
        // 09:00–12:00 and dodge the 10:00–11:00 booking. Only 09:00 (ends
        // exactly 10:00) and 11:00 survive.
        Assert.Equal(
            new[] { new TimeSpan(9, 0, 0), new TimeSpan(11, 0, 0) },
            localStarts);
    }

    // 2 ------------------------------------------------------------------
    [Fact]
    public async Task Create_Success_PersistsWithComputedEnd()
    {
        var date = FutureDate;
        var basics = await _fx.SeedBasicsAsync(date, durationMinutes: 45);
        var start = LocalToUtc(date, 10);

        using var scope = _fx.NewScope(basics.TenantId);
        var result = await scope.Bookings.CreateAsync(
            basics.ServiceId, basics.StaffId, "Alice Tan", "alice@example.com", start);

        Assert.Equal(BookingOutcome.Success, result.Outcome);
        Assert.NotNull(result.Booking);

        using var verify = _fx.NewScope(basics.TenantId);
        var saved = await verify.Db.Bookings.SingleAsync(b => b.Id == result.Booking!.Id);
        Assert.Equal(start, saved.StartUtc);
        Assert.Equal(start.AddMinutes(45), saved.EndUtc);
        Assert.Equal(BookingStatus.Confirmed, saved.Status);
        Assert.Equal("Alice Tan", saved.CustomerName);
    }

    // 3 ------------------------------------------------------------------
    [Fact]
    public async Task Create_SequentialOverlap_ReturnsSlotTaken_AdjacentAllowed()
    {
        var date = FutureDate;
        var basics = await _fx.SeedBasicsAsync(date, durationMinutes: 60);
        using var scope = _fx.NewScope(basics.TenantId);

        var first = await scope.Bookings.CreateAsync(
            basics.ServiceId, basics.StaffId, "A", "a@x.com", LocalToUtc(date, 10));
        Assert.Equal(BookingOutcome.Success, first.Outcome);

        var overlapping = await scope.Bookings.CreateAsync(
            basics.ServiceId, basics.StaffId, "B", "b@x.com", LocalToUtc(date, 10, 30));
        Assert.Equal(BookingOutcome.SlotTaken, overlapping.Outcome);

        // Half-open intervals: starting exactly when the first ends is fine.
        var adjacent = await scope.Bookings.CreateAsync(
            basics.ServiceId, basics.StaffId, "C", "c@x.com", LocalToUtc(date, 11));
        Assert.Equal(BookingOutcome.Success, adjacent.Outcome);
    }

    // 4 ------------------------------------------------------------------
    [Fact]
    public async Task DatabaseConstraint_BlocksDirectOverlappingInsert()
    {
        var date = FutureDate;
        var basics = await _fx.SeedBasicsAsync(date, durationMinutes: 60);

        using (var scope = _fx.NewScope(basics.TenantId))
        {
            var booked = await scope.Bookings.CreateAsync(
                basics.ServiceId, basics.StaffId, "A", "a@x.com", LocalToUtc(date, 10));
            Assert.Equal(BookingOutcome.Success, booked.Outcome);
        }

        // Bypass the service entirely: raw SQL straight at the table.
        await using var conn = new NpgsqlConnection(_fx.ConnectionString);
        await conn.OpenAsync();

        async Task InsertRawAsync(DateTimeOffset start, DateTimeOffset end, int status)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO "Bookings"
                    ("Id", "TenantId", "ServiceId", "StaffMemberId", "CustomerName",
                     "CustomerContact", "StartUtc", "EndUtc", "Status", "ReminderSentAt", "CreatedAt")
                VALUES
                    (@id, @tenant, @service, @staff, 'Raw', 'raw@x.com',
                     @start, @end, @status, NULL, @created)
                """, conn);
            cmd.Parameters.AddWithValue("id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("tenant", basics.TenantId);
            cmd.Parameters.AddWithValue("service", basics.ServiceId);
            cmd.Parameters.AddWithValue("staff", basics.StaffId);
            cmd.Parameters.AddWithValue("start", start);
            cmd.Parameters.AddWithValue("end", end);
            cmd.Parameters.AddWithValue("status", status);
            cmd.Parameters.AddWithValue("created", DateTimeOffset.UtcNow);
            await cmd.ExecuteNonQueryAsync();
        }

        // Overlapping Confirmed (Status = 0): the exclusion constraint fires.
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertRawAsync(LocalToUtc(date, 10, 30), LocalToUtc(date, 11, 30), status: 0));
        Assert.Equal("23P01", ex.SqlState);

        // Overlapping but Cancelled (Status = 1): the partial constraint
        // ignores it, so the insert succeeds.
        await InsertRawAsync(LocalToUtc(date, 10, 30), LocalToUtc(date, 11, 30), status: 1);

        // Adjacent Confirmed (starts exactly at the first booking's end):
        // tstzrange's [) bounds mean no conflict.
        await InsertRawAsync(LocalToUtc(date, 11), LocalToUtc(date, 12), status: 0);
    }

    // 5 ------------------------------------------------------------------
    [Fact]
    public async Task ParallelRace_ExactlyOneWins()
    {
        var date = FutureDate;
        var basics = await _fx.SeedBasicsAsync(date, durationMinutes: 60);
        var start = LocalToUtc(date, 14);

        var attempts = Enumerable.Range(0, 10).Select(async i =>
        {
            using var scope = _fx.NewScope(basics.TenantId);
            return await scope.Bookings.CreateAsync(
                basics.ServiceId, basics.StaffId, $"Racer {i}", $"r{i}@x.com", start);
        });
        var results = await Task.WhenAll(attempts);

        Assert.Equal(1, results.Count(r => r.Outcome == BookingOutcome.Success));
        Assert.Equal(9, results.Count(r => r.Outcome == BookingOutcome.SlotTaken));

        using var verify = _fx.NewScope(basics.TenantId);
        var confirmed = await verify.Db.Bookings
            .CountAsync(b => b.StaffMemberId == basics.StaffId && b.Status == BookingStatus.Confirmed);
        Assert.Equal(1, confirmed);
    }

    // 6 ------------------------------------------------------------------
    [Fact]
    public async Task Cancel_FreesTheSlot()
    {
        var date = FutureDate;
        var basics = await _fx.SeedBasicsAsync(date, durationMinutes: 60);
        var start = LocalToUtc(date, 10);
        using var scope = _fx.NewScope(basics.TenantId);

        var booked = await scope.Bookings.CreateAsync(
            basics.ServiceId, basics.StaffId, "A", "a@x.com", start);
        Assert.Equal(BookingOutcome.Success, booked.Outcome);

        var slotsWhileBooked = await scope.Bookings.GetSlotsAsync(basics.ServiceId, basics.StaffId, date);
        Assert.DoesNotContain(slotsWhileBooked.Slots, s => s.StartUtc == start);

        var cancel = await scope.Bookings.CancelAsync(booked.Booking!.Id);
        Assert.Equal(CancelOutcome.Cancelled, cancel);

        var slotsAfterCancel = await scope.Bookings.GetSlotsAsync(basics.ServiceId, basics.StaffId, date);
        Assert.Contains(slotsAfterCancel.Slots, s => s.StartUtc == start);

        var rebooked = await scope.Bookings.CreateAsync(
            basics.ServiceId, basics.StaffId, "B", "b@x.com", start);
        Assert.Equal(BookingOutcome.Success, rebooked.Outcome);
    }

    // 7 ------------------------------------------------------------------
    [Fact]
    public async Task Create_InPast_Rejected()
    {
        var date = FutureDate;
        var basics = await _fx.SeedBasicsAsync(date);
        using var scope = _fx.NewScope(basics.TenantId);

        var result = await scope.Bookings.CreateAsync(
            basics.ServiceId, basics.StaffId, "A", "a@x.com",
            DateTimeOffset.UtcNow.AddHours(-1));

        Assert.Equal(BookingOutcome.InPast, result.Outcome);
    }

    [Fact]
    public async Task Create_OutsideWorkingHours_Rejected()
    {
        var date = FutureDate;
        var basics = await _fx.SeedBasicsAsync(date, durationMinutes: 60,
            opens: new TimeOnly(9, 0), closes: new TimeOnly(18, 0));
        using var scope = _fx.NewScope(basics.TenantId);

        var evening = await scope.Bookings.CreateAsync(
            basics.ServiceId, basics.StaffId, "A", "a@x.com", LocalToUtc(date, 20));
        Assert.Equal(BookingOutcome.OutsideHours, evening.Outcome);

        var early = await scope.Bookings.CreateAsync(
            basics.ServiceId, basics.StaffId, "A", "a@x.com", LocalToUtc(date, 8));
        Assert.Equal(BookingOutcome.OutsideHours, early.Outcome);

        // Starts inside hours but runs past closing (17:30 + 60m > 18:00).
        var overrun = await scope.Bookings.CreateAsync(
            basics.ServiceId, basics.StaffId, "A", "a@x.com", LocalToUtc(date, 17, 30));
        Assert.Equal(BookingOutcome.OutsideHours, overrun.Outcome);
    }

    // 8 ------------------------------------------------------------------
    [Fact]
    public async Task TenantIsolation_FiltersAndForeignIds()
    {
        var date = FutureDate;
        var a = await _fx.SeedBasicsAsync(date);
        var b = await _fx.SeedBasicsAsync(date);

        using (var scopeA = _fx.NewScope(a.TenantId))
        {
            var booked = await scopeA.Bookings.CreateAsync(
                a.ServiceId, a.StaffId, "A", "a@x.com", LocalToUtc(date, 10));
            Assert.Equal(BookingOutcome.Success, booked.Outcome);
        }

        using var scopeB = _fx.NewScope(b.TenantId);
        // B's filtered view: none of A's bookings, and A's service is invisible.
        Assert.Equal(0, await scopeB.Db.Bookings.CountAsync());
        Assert.DoesNotContain(await scopeB.Db.Services.ToListAsync(), s => s.Id == a.ServiceId);
        Assert.DoesNotContain(await scopeB.Db.StaffMembers.ToListAsync(), s => s.Id == a.StaffId);

        // Booking under B using A's service id reads as NotFound, not a leak.
        var cross = await scopeB.Bookings.CreateAsync(
            a.ServiceId, b.StaffId, "Sneaky", "s@x.com", LocalToUtc(date, 12));
        Assert.Equal(BookingOutcome.NotFound, cross.Outcome);
    }

    // 9 ------------------------------------------------------------------
    [Fact]
    public async Task ReminderQuery_ReturnsOnlyUpcomingUnremindedConfirmed()
    {
        var basics = await _fx.SeedBasicsAsync(FutureDate);
        var now = DateTimeOffset.UtcNow;

        var dueSoon = await _fx.InsertBookingAsync(basics.TenantId, basics.ServiceId, basics.StaffId,
            now.AddHours(2), now.AddHours(2.5));
        var alreadyReminded = await _fx.InsertBookingAsync(basics.TenantId, basics.ServiceId, basics.StaffId,
            now.AddHours(3), now.AddHours(3.5), reminderSentAt: now);
        var tooFarOut = await _fx.InsertBookingAsync(basics.TenantId, basics.ServiceId, basics.StaffId,
            now.AddHours(48), now.AddHours(48.5));
        var cancelledSoon = await _fx.InsertBookingAsync(basics.TenantId, basics.ServiceId, basics.StaffId,
            now.AddHours(1), now.AddHours(1.5), status: BookingStatus.Cancelled);
        var inThePast = await _fx.InsertBookingAsync(basics.TenantId, basics.ServiceId, basics.StaffId,
            now.AddHours(-2), now.AddHours(-1.5));

        using var scope = _fx.NewScope(null);
        var due = await scope.Bookings.GetUpcomingRemindersAsync(withinHours: 24);
        var ids = due.Select(b => b.Id).ToList();

        Assert.Contains(dueSoon, ids);
        Assert.DoesNotContain(alreadyReminded, ids);
        Assert.DoesNotContain(tooFarOut, ids);
        Assert.DoesNotContain(cancelledSoon, ids);
        Assert.DoesNotContain(inThePast, ids);
    }
}
