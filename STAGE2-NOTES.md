# Slotwise — Stage 2 notes (the face)

Stage 2 of the plan in `~/workspace/your_files/fourth-showcase-plan.md`:
the public booking flow, the admin area with cookie auth, the reminder
worker, and seed-on-empty — on top of the stage-1 core (booking engine,
tenant filters, exclusion constraint). Tests: **15/15 passing**
(10 stage-1 + 5 new stage-2).

## Pages / routes

Public (no login):

| Route | What it does |
|---|---|
| `GET /` | Landing page; links to the demo tenant's booking page (when seeded) and the admin login |
| `GET /book/{slug}` | The tenant's booking page — one page, three states driven by query params (see below) |
| Stage-1 API | `/healthz`, `/api/book/...`, `/api/tenants/...`, `/api/dev/seed` — unchanged, except dev/seed now delegates to `DemoSeeder` |

Admin (cookie auth required; everything under `/admin` except login/logout):

| Route | What it does |
|---|---|
| `GET/POST /admin/login` | Email + password against `TenantAdmin` (see Auth) |
| `POST /admin/logout` | Signs out (GET works too) |
| `GET /admin` | Dashboard: count strip (bookings today / upcoming / reminders sent), today's bookings table, next-10 upcoming |
| `GET /admin/bookings` | All bookings with `Date` (tenant-local day; empty = all dates) and `Status` filters; per-row actions below |
| `POST /admin/bookings?handler=Cancel` | Cancels via stage-1 `BookingService.CancelAsync` — the slot frees immediately |
| `POST /admin/bookings?handler=Complete` / `?handler=NoShow` | State transitions, guarded (only from Confirmed) |
| `GET/POST /admin/services` | List + create/edit (name, duration minutes, price in S$, active toggle) |
| `GET/POST /admin/staff` | List + create/edit (name, active toggle) |
| `GET/POST /admin/staff/hours/{staffId}` | Weekly hours editor: per weekday an Open checkbox + start/end times; save replaces the whole week |

### The public booking flow

One Razor page, three states, all plain GET links / PRG redirects — no
session, no JS framework (the pickers auto-submit via a one-line
`onchange`, with a `<noscript>` button fallback):

1. **Select** — `?serviceId&staffId&date` (date defaults to today,
   clamped to today…today+14). Available slots from
   `BookingService.GetSlotsAsync` render as pill links in tenant-local time.
2. **Confirm** — `&slot=<startUtc>` adds a summary card + name/contact
   form. The slot is re-validated against a fresh slot list; if it's gone,
   the page falls back to Select with a flash: *"That slot was just
   taken — please pick another time below."*
3. **Done** — POST → `CreateAsync` → redirect to `?booked=<id>`, which
   renders the confirmed summary. If the POST loses the race
   (`SlotTaken`), it redirects back to Select with the same friendly
   flash and fresh slots — never an error page. `InPast`/`OutsideHours`
   get the equivalent "no longer available" flash.

Unknown slug → HTTP 404 rendered in the same layout/style
("We couldn't find that business"), not the framework default.

## Auth approach

- `TenantAdmin` entity (new migration `AddTenantAdmin`): Id, TenantId,
  Email (unique across the platform), PasswordHash, CreatedAt. Tenant
  query filter applies like every other tenant-owned entity.
- Passwords are hashed with ASP.NET Core's `PasswordHasher<TenantAdmin>`
  (PBKDF2). Plaintext is never stored; the one demo password exists only
  as a seeder constant (below) so the notes and seeder can't drift apart.
- Cookie authentication (`Slotwise.Admin` cookie, 8 h sliding). The
  sign-in principal carries the tenant id/slug/name as claims
  (`slotwise:tenant_id` etc.).
- `TenantClaimMiddleware` (registered after `UseAuthentication`) copies
  the tenant-id claim into the scoped `ITenantContext`, so the stage-1
  EF global query filters scope **every** admin query to the admin's
  tenant — page code never passes a TenantId. The only cross-tenant
  reads are the login lookup itself (`IgnoreQueryFilters` on the unique
  email) and the reminder scan, both deliberate and commented.
- Razor Pages conventions do the guarding: `AuthorizeFolder("/Admin")`
  with `AllowAnonymousToPage` for Login/Logout only.

## Reminder design

`ReminderService : BackgroundService` (`Slotwise/Services/`):

- Loop: scan, then delay `Reminders:IntervalMinutes` (default **5**),
  resilient to failures (a failed scan logs and retries next interval).
- Each pass calls the stage-1 cross-tenant `GetUpcomingRemindersAsync`
  with `Reminders:WindowHours` (default **2**): Confirmed, `StartUtc` in
  (now, now+window], `ReminderSentAt == null`. It stamps
  `ReminderSentAt = now` and logs one line per reminder, e.g.
  `Reminder sent: Aisha Rahman (aisha@example.com) at Lakeside Dental — Checkup, 2026-11-26 3:00 AM.`
- No email/SMS is sent (v1 scope). The visible evidence is the stamp:
  the dashboard's "Reminders sent" count and a **Reminder sent** badge
  on booking rows.
- `ScanOnceAsync` is public and timer-free, so tests (and operators) can
  trigger exactly one pass.

Verified live: on a fresh database the worker's first scan marked the
seeded due-soon booking; the log line above is real output, and the DB
row shows `ReminderSentAt` set while later bookings remain unstamped.

## Seed data (seed-on-empty)

At startup, after migrations, if there are **no tenants at all** the app
runs `DemoSeeder` — the same code path `POST /api/dev/seed` delegates to
(idempotent: an existing tenant gets any missing admin/bookings topped
up). Demo tenant: **Lakeside Dental** (`lakeside-dental`, Asia/Singapore):

- Services: Checkup 30 min · S$50, Cleaning 60 min · S$80
- Staff: Dr. Chen, Dr. Patel — Mon–Sat 09:00–18:00
- **Admin login (demo only): `admin@lakeside-dental.test` / `SlotwiseDemo1`**
- ~8 bookings across today and tomorrow (tenant-local): one Confirmed
  starting at the next half-hour boundary (always inside the 2 h reminder
  window, so the worker has something to mark on its first scan), one
  later today if it fits working hours, one Completed from this morning
  if that's already past, one Cancelled at 15:00 today, and four
  Confirmed tomorrow (09:30 / 11:00 / 14:30 / 16:00). Time-dependent
  entries skip gracefully when they don't fit (e.g. seeding at 03:00
  yields 6 bookings), and a per-staff overlap guard keeps the Confirmed
  set consistent. Bookings are inserted directly (demo data), so the
  due-soon one can sit outside working hours when seeding happens at
  night — noted here rather than hidden.

## Deploy readiness (stage 3)

- `PathBase` config (env `PathBase`) → `UsePathBase` when set. All links
  and forms use tag helpers / generated URLs, so the app works unchanged
  under `/slotwise`: verified manually — with `PathBase=/slotwise` the
  booking page, slot links and admin login all resolve under the prefix.
- Connection string stays configuration-driven
  (`ConnectionStrings:Slotwise`, falling back to `...:Default`), so the
  Docker deploy overrides it via `ConnectionStrings__Slotwise` as before.

## Look

Server-rendered Razor with one embedded stylesheet (a `_Styles` partial
included by both layouts — no build step, no CDN, no external fonts).
Dark slate theme in Settle's family but with a **teal accent** (#2dd4bf)
plus sky links, so the two products are distinguishable at a glance.
Status badges are colour-coded (Confirmed teal, Cancelled red, Completed
sky, NoShow amber; reminders get a filled amber pill). Tables sit in
overflow wrappers so the layout holds up on a phone.

## Tests — 15/15 (`dotnet test`, ~15 s)

Stage-1's 10 are untouched and still green. New in `Stage2Tests`
(WebApplicationFactory against a per-run database; the app's own
seed-on-empty provides the demo tenant; a cookie-jar + antiforgery-aware
test client drives the real forms):

| # | Test | Proves |
|---|------|--------|
| 11 | PublicFlow_BookASlot_EndsConfirmed | GET booking page → slot link → confirm page → POST form → success page; booking persisted as Confirmed with the exact start |
| 12 | Admin_RequiresLogin_AndSeededAdminCanSignIn | `/admin` 302s to login when anonymous; the seeded admin signs in and the dashboard renders |
| 13 | AdminCancel_FreesTheSlotAgain | Book via API → slot disappears → cancel in admin → booking is Cancelled and the slot is bookable again |
| 14 | ReminderScan_MarksOnlyDueBookings | One `ScanOnceAsync` stamps a booking starting in 1 h and leaves one starting in 5 h unstamped |
| 15 | PublicFlow_UnknownSlug_IsAStyled404 | Unknown slug → 404 with the styled not-found page |

Manual verification (fresh `slotwise_stage2` DB, app on :5055):
`/healthz` 200; booking page rendered with 35 slot links; curl login
(cookie jar + antiforgery token) → 302 → dashboard 200 showing
2 bookings today / 5 upcoming / 1 reminder sent with the seeded names;
`POST /api/dev/seed` → `created:false` with the full payload.

## Deviations / gotchas found in stage 2

- **WebApplicationFactory config override:** an in-memory collection
  added via `WithWebHostBuilder(ConfigureAppConfiguration)` did **not**
  outrank `appsettings.json` for this minimal-hosting app — the first
  test run silently ran against the dev database. The stage-2 tests
  instead set `ConnectionStrings__Slotwise` / `Reminders__IntervalMinutes`
  as **environment variables** before creating the factory (env config
  outranks appsettings). Documented in the test class.
- **The sandbox Postgres died mid-stage** (a runtime service restart
  killed the namespaced server). Restarted exactly as stage 1 ran it:
  `unshare --map-user=1000 --map-group=1000 pg_ctl -D ~/pg16/data -o "-p 5434 -k /home/hatch/pg16/run -h 127.0.0.1" start` — all data intact.
- Razor Pages' `RedirectToPage("/Admin/Index")` generates `/Admin`
  (capital A) while the cookie `LoginPath` is lowercase `/admin/login`;
  routing is case-insensitive so both work — noted because a test
  asserted the lowercase form.
- The seeded booking count is "≈8" by design (see Seed data): entries
  tied to working hours skip when the seed runs outside them.
