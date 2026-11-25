using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Slotwise.Domain;
using Slotwise.Services;

namespace Slotwise.Tests;

/// <summary>
/// Stage-2 (UI) tests: the whole app hosted in-memory by
/// WebApplicationFactory against a per-run database. The app's own
/// startup seed-on-empty provides the Lakeside Dental demo tenant, its
/// admin login and its sample bookings, exactly as in production.
/// The reminder worker's background interval is stretched to 60 minutes
/// so tests drive it explicitly through ScanOnceAsync.
/// </summary>
public class Stage2Tests : IClassFixture<TestDbFixture>, IDisposable
{
    private static readonly TimeZoneInfo SgTz =
        TimeZoneInfo.FindSystemTimeZoneById("Asia/Singapore");

    private readonly TestDbFixture _fx;
    private readonly WebApplicationFactory<Program> _factory;

    public Stage2Tests(TestDbFixture fx)
    {
        _fx = fx;
        // Point the app at this run's throwaway database via environment
        // variables: WebApplication.CreateBuilder's environment provider
        // outranks appsettings.json, whereas a ConfigureAppConfiguration
        // in-memory collection added through WebApplicationFactory does
        // not reliably win for a minimal-hosting app (verified the hard
        // way: the app happily used the dev database instead).
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__Slotwise", fx.ConnectionString);
        Environment.SetEnvironmentVariable("Reminders__IntervalMinutes", "60");
        _factory = new WebApplicationFactory<Program>();
    }

    public void Dispose() => _factory.Dispose();

    // (a) ---------------------------------------------------------------
    [Fact]
    public async Task PublicFlow_BookASlot_EndsConfirmed()
    {
        var seeded = await SeededAsync();
        var date = NextOpenDay(DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, SgTz).DateTime).AddDays(1));

        var browser = new BrowserClient(_factory);
        var page = await browser.GetAsync(
            $"/book/lakeside-dental?serviceId={seeded.CheckupId}&staffId={seeded.ChenId}&date={date:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, page.Status);
        Assert.Contains("Lakeside Dental", page.Body);

        var slotMatch = Regex.Match(page.Body, "class=\"slot\" href=\"([^\"]+)\"");
        Assert.True(slotMatch.Success, "no available-slot links were rendered");
        var confirmUrl = WebUtility.HtmlDecode(slotMatch.Groups[1].Value);

        var confirm = await browser.GetAsync(confirmUrl);
        Assert.Equal(HttpStatusCode.OK, confirm.Status);
        Assert.Contains("Confirm your booking", confirm.Body);

        var fields = ExtractHiddenFields(confirm.Body);
        var expectedStart = DateTimeOffset.Parse(fields["FormSlot"]);
        fields["FormName"] = "Test Customer";
        fields["FormContact"] = "test@example.com";

        var post = await browser.PostFormAsync(
            "/book/lakeside-dental?handler=Book", confirm.Body, fields);
        Assert.Equal(HttpStatusCode.Found, post.Status);
        Assert.Contains("booked=", post.Location);

        var done = await browser.GetAsync(post.Location!);
        Assert.Equal(HttpStatusCode.OK, done.Status);
        Assert.Contains("You're booked in", done.Body);
        Assert.Contains("Test Customer", done.Body);

        using var scope = _fx.NewScope(seeded.TenantId);
        var booking = await scope.Db.Bookings
            .FirstOrDefaultAsync(b => b.CustomerName == "Test Customer");
        Assert.NotNull(booking);
        Assert.Equal(BookingStatus.Confirmed, booking.Status);
        Assert.Equal(seeded.CheckupId, booking.ServiceId);
        Assert.Equal(seeded.ChenId, booking.StaffMemberId);
        Assert.Equal(expectedStart, booking.StartUtc);
    }

    // (b) ---------------------------------------------------------------
    [Fact]
    public async Task Admin_RequiresLogin_AndSeededAdminCanSignIn()
    {
        var browser = new BrowserClient(_factory);
        var guarded = await browser.GetAsync("/admin");
        Assert.Equal(HttpStatusCode.Found, guarded.Status);
        Assert.Contains("/admin/login", guarded.Location);

        var loggedIn = await LoginAsync();
        var dashboard = await loggedIn.GetAsync("/admin");
        Assert.Equal(HttpStatusCode.OK, dashboard.Status);
        Assert.Contains("Lakeside Dental", dashboard.Body);
        Assert.Contains("Bookings today", dashboard.Body);
        Assert.Contains("Reminders sent", dashboard.Body);
    }

    // (c) ---------------------------------------------------------------
    [Fact]
    public async Task AdminCancel_FreesTheSlotAgain()
    {
        var seeded = await SeededAsync();
        var todayLocal = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, SgTz).DateTime);
        var date = NextOpenDay(todayLocal.AddDays(3));

        DateTimeOffset slotStart;
        using (var scope = _fx.NewScope(seeded.TenantId))
        {
            var slots = await scope.Bookings.GetSlotsAsync(
                seeded.CheckupId, seeded.PatelId, date);
            Assert.NotEmpty(slots.Slots);
            slotStart = slots.Slots[0].StartUtc;
        }

        // Book the slot through the public API.
        using var http = _factory.CreateClient();
        var created = await http.PostAsJsonAsync("/api/book/lakeside-dental", new
        {
            ServiceId = seeded.CheckupId,
            StaffId = seeded.PatelId,
            CustomerName = "Cancel Me",
            CustomerContact = "cancel@example.com",
            StartUtc = slotStart,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        Guid bookingId;
        using (var scope = _fx.NewScope(seeded.TenantId))
        {
            var after = await scope.Bookings.GetSlotsAsync(
                seeded.CheckupId, seeded.PatelId, date);
            Assert.DoesNotContain(after.Slots, s => s.StartUtc == slotStart);
            bookingId = (await scope.Db.Bookings
                .FirstAsync(b => b.CustomerName == "Cancel Me")).Id;
        }

        // Cancel it from the admin bookings page.
        var browser = await LoginAsync();
        var list = await browser.GetAsync($"/admin/bookings?Date={date:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, list.Status);
        Assert.Contains("Cancel Me", list.Body);

        var cancel = await browser.PostFormAsync(
            "/admin/bookings?handler=Cancel", list.Body,
            new Dictionary<string, string>
            {
                ["bookingId"] = bookingId.ToString(),
                ["Date"] = date.ToString("yyyy-MM-dd"),
                ["Status"] = "",
            });
        Assert.Equal(HttpStatusCode.Found, cancel.Status);

        using var verify = _fx.NewScope(seeded.TenantId);
        var booking = await verify.Db.Bookings.FirstAsync(b => b.Id == bookingId);
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        var freed = await verify.Bookings.GetSlotsAsync(
            seeded.CheckupId, seeded.PatelId, date);
        Assert.Contains(freed.Slots, s => s.StartUtc == slotStart);
    }

    // (d) ---------------------------------------------------------------
    [Fact]
    public async Task ReminderScan_MarksOnlyDueBookings()
    {
        var seeded = await SeededAsync();
        var now = DateTimeOffset.UtcNow;

        var dueId = await _fx.InsertBookingAsync(
            seeded.TenantId, seeded.CheckupId, seeded.PatelId,
            now.AddHours(1), now.AddHours(1.5));
        var notDueId = await _fx.InsertBookingAsync(
            seeded.TenantId, seeded.CheckupId, seeded.PatelId,
            now.AddHours(5), now.AddHours(5.5));

        var reminders = _factory.Services.GetServices<IHostedService>()
            .OfType<ReminderService>().Single();
        await reminders.ScanOnceAsync();

        using var scope = _fx.NewScope(seeded.TenantId);
        var due = await scope.Db.Bookings.FirstAsync(b => b.Id == dueId);
        var notDue = await scope.Db.Bookings.FirstAsync(b => b.Id == notDueId);
        Assert.NotNull(due.ReminderSentAt);
        Assert.Null(notDue.ReminderSentAt);
    }

    // (e) ---------------------------------------------------------------
    [Fact]
    public async Task PublicFlow_UnknownSlug_IsAStyled404()
    {
        using var http = _factory.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await http.GetAsync("/book/no-such-business");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("couldn't find that business", body);
    }

    // --- helpers ---------------------------------------------------------

    private static DateOnly NextOpenDay(DateOnly date)
    {
        while (date.DayOfWeek == DayOfWeek.Sunday)
            date = date.AddDays(1);
        return date;
    }

    /// <summary>Starts the app (first client creation runs startup,
    /// including seed-on-empty) and returns the seeded demo ids.</summary>
    private async Task<(Guid TenantId, Guid CheckupId, Guid ChenId, Guid PatelId)> SeededAsync()
    {
        using (var http = _factory.CreateClient())
        {
            var health = await http.GetAsync("/healthz");
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }

        using var unscoped = _fx.NewScope(null);
        var tenant = await unscoped.Db.Tenants.IgnoreQueryFilters()
            .FirstAsync(t => t.Slug == DemoSeeder.DemoSlug);

        using var scope = _fx.NewScope(tenant.Id);
        var checkup = await scope.Db.Services.FirstAsync(s => s.Name == "Checkup");
        var chen = await scope.Db.StaffMembers.FirstAsync(s => s.Name == "Dr. Chen");
        var patel = await scope.Db.StaffMembers.FirstAsync(s => s.Name == "Dr. Patel");
        return (tenant.Id, checkup.Id, chen.Id, patel.Id);
    }

    private async Task<BrowserClient> LoginAsync()
    {
        var browser = new BrowserClient(_factory);
        var login = await browser.GetAsync("/admin/login");
        Assert.Equal(HttpStatusCode.OK, login.Status);

        var post = await browser.PostFormAsync("/admin/login", login.Body,
            new Dictionary<string, string>
            {
                ["Email"] = DemoSeeder.DemoAdminEmail,
                ["Password"] = DemoSeeder.DemoAdminPassword,
            });
        Assert.Equal(HttpStatusCode.Found, post.Status);
        Assert.Contains("/admin", post.Location, StringComparison.OrdinalIgnoreCase);
        return browser;
    }

    private static Dictionary<string, string> ExtractHiddenFields(string html)
    {
        // Attribute order in rendered <input> tags varies (id, data-val,
        // …), so match the whole tag first, then pull name/value out of it.
        var fields = new Dictionary<string, string>();
        foreach (Match tag in Regex.Matches(html, """<input\b[^>]*type="hidden"[^>]*>"""))
        {
            var name = Regex.Match(tag.Value, "name=\"([^\"]+)\"");
            var value = Regex.Match(tag.Value, "value=\"([^\"]*)\"");
            if (name.Success && name.Groups[1].Value != "__RequestVerificationToken")
            {
                fields[name.Groups[1].Value] = WebUtility.HtmlDecode(
                    value.Success ? value.Groups[1].Value : "");
            }
        }
        return fields;
    }

    /// <summary>
    /// A minimal cookie-jar + antiforgery-aware client for the Razor
    /// Pages flows: GETs a page, then POSTs a form using the antiforgery
    /// token embedded in that page's HTML.
    /// </summary>
    private sealed class BrowserClient
    {
        private readonly HttpClient _http;
        private readonly Dictionary<string, string> _cookies = new();

        public BrowserClient(WebApplicationFactory<Program> factory)
        {
            _http = factory.CreateClient(
                new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        }

        public async Task<(HttpStatusCode Status, string Body, string? Location)>
            GetAsync(string url)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddCookies(request);
            using var response = await _http.SendAsync(request);
            StoreCookies(response);
            return (response.StatusCode,
                await response.Content.ReadAsStringAsync(),
                response.Headers.Location?.ToString());
        }

        public async Task<(HttpStatusCode Status, string Body, string? Location)>
            PostFormAsync(string url, string htmlWithToken,
                IEnumerable<KeyValuePair<string, string>> fields)
        {
            var form = new List<KeyValuePair<string, string>>(fields)
            {
                new("__RequestVerificationToken", ExtractToken(htmlWithToken)),
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new FormUrlEncodedContent(form),
            };
            AddCookies(request);
            using var response = await _http.SendAsync(request);
            StoreCookies(response);
            return (response.StatusCode,
                await response.Content.ReadAsStringAsync(),
                response.Headers.Location?.ToString());
        }

        private static string ExtractToken(string html)
        {
            var match = Regex.Match(html,
                "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
            Assert.True(match.Success, "antiforgery token not found in page HTML");
            return match.Groups[1].Value;
        }

        private void AddCookies(HttpRequestMessage request)
        {
            if (_cookies.Count > 0)
            {
                request.Headers.TryAddWithoutValidation("Cookie",
                    string.Join("; ", _cookies.Select(kv => $"{kv.Key}={kv.Value}")));
            }
        }

        private void StoreCookies(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Set-Cookie", out var values))
                return;
            foreach (var value in values)
            {
                var pair = value.Split(';')[0];
                var eq = pair.IndexOf('=');
                if (eq > 0)
                    _cookies[pair[..eq].Trim()] = pair[(eq + 1)..].Trim();
            }
        }
    }
}
