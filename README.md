# slotwise

A multi-tenant appointment booking platform in **ASP.NET Core + EF Core +
PostgreSQL**. Small businesses — clinics, salons, tuition centres — get a
public booking page and an admin dashboard; customers pick a service, a
staff member and a real available slot, and book without an account.

The interesting problem in booking is not the CRUD. It's the race: two
customers, one last slot, same moment. Slotwise's answer is that the
guarantee doesn't live in the application code — **the database itself
refuses to hold two overlapping bookings for the same staff member**, so
no bug, retry or race in the app can double-book anyone.

**Live demo:** the app is running on a VPS at
http://5.222.0.70/slotwise/book/lakeside-dental — book a slot at the demo
clinic, then try to take the same one again. The business side is at
http://5.222.0.70/slotwise/admin (demo-only login:
`admin@lakeside-dental.test` / `SlotwiseDemo1`).

## Demo

Two views of the same system — the product first, then the proof underneath.

**The UI walkthrough** — booking a cleaning appointment on the live site: pick a slot, confirm, and then watch that slot disappear from the day for everyone else — ending with the same booking sitting in the business's admin list.

https://github.com/user-attachments/assets/60873d6b-0893-40e9-a09b-f90739908dd3

**The API in a terminal** — the same guarantees over HTTP against the live server: fetch the day's slots, book one, send the identical booking again and get refused, cancel, and see the slot come back.

https://github.com/user-attachments/assets/5dd1c336-ff8b-49d3-bdad-3767ba1eb9f3

## How it works

```
customer picks a slot ──▶ BookingService.CreateAsync (one transaction)
                            validate service + staff (tenant-scoped)
                            reject: start in the past
                            check [start, start+duration) fits the staff
                                  member's working hours (tenant-local)
                            check no overlapping Confirmed booking
                            insert ──▶ PostgreSQL exclusion constraint
                                       re-checks the same rule, atomically
                          commit — or a typed refusal, nothing written
```

**Slots are computed, not stored.** For a given service, staff member and
day, the engine takes the staff member's working-hours window, cuts it
into slots by the service's duration, and subtracts anything a Confirmed
booking overlaps. All times are stored as UTC `timestamptz` and
interpreted in the tenant's own timezone (`Asia/Singapore` for the demo
clinic) at the boundary.

**The constraint.** Migration `AddNoOverlapExclusionConstraint` adds,
via raw SQL:

```sql
CREATE EXTENSION IF NOT EXISTS btree_gist;
ALTER TABLE "Bookings"
ADD CONSTRAINT "EX_Bookings_NoOverlap"
EXCLUDE USING gist (
    "StaffMemberId" WITH =,
    tstzrange("StartUtc", "EndUtc") WITH &&
)
WHERE ("Status" = 0);   -- Confirmed bookings only
```

Half-open `[)` ranges mean back-to-back bookings are fine, and the
partial `WHERE` means a cancelled booking frees its slot the moment it's
cancelled — no cleanup job, no stale state.

## Design decisions worth talking about

- **Double-booking is prevented twice, on purpose.** The app checks for
  overlap inside the booking transaction and returns a friendly refusal.
  But under READ COMMITTED, two racing transactions can both pass that
  check — so the exclusion constraint is the real guarantee. The loser's
  INSERT fails with SQLSTATE **23P01** (`exclusion_violation`), which the
  service catches and translates into the same typed `SlotTaken` outcome.
  The test suite proves it twice over: a raw SQL insert that bypasses
  the service entirely is rejected by the database, and 10 concurrent
  booking attempts for one slot produce exactly 1 success and 9 clean
  `SlotTaken` refusals.
- **Typed outcomes, not exceptions.** `BookingService` returns a result
  with outcome Success / SlotTaken / OutsideHours / InPast / NotFound;
  the API maps them to 201 / 409 / 422 / 422 / 404 and the booking page
  maps them to plain-language flashes ("That slot was just taken —
  please pick another time"). Expected failures never surface as a 500
  or an error page.
- **Tenancy is enforced in one place.** Every tenant-owned entity has an
  EF Core global query filter driven by a scoped `ITenantContext`. API
  routes resolve the tenant by slug once; admin pages get their tenant
  from the login's claims via middleware. Page and endpoint code never
  passes a tenant id around, so there's no per-query filter to forget —
  and with no tenant selected, the filters fail closed and match
  nothing. Exactly two reads deliberately bypass the filters, both
  commented: the login lookup (by globally unique email, before any
  tenant exists) and the reminder scan (a background job has no current
  tenant).
- **Reminders are a `BackgroundService`, not a cron afterthought.**
  `ReminderService` wakes every 5 minutes (configurable), finds
  Confirmed bookings starting within the next 2 hours that haven't been
  reminded, and stamps `ReminderSentAt` on them — one deliberate
  cross-tenant query, exposed as a public `ScanOnceAsync` so tests drive
  exactly one pass. No email or SMS leaves the system in v1; the stamp
  is the evidence, visible as a badge in the admin dashboard.
- **Passwords hashed, never stored.** Admin logins use ASP.NET Core's
  `PasswordHasher<TenantAdmin>` (PBKDF2) with cookie authentication.
  The one plaintext demo password exists only as a seeder constant so
  the demo login above can't drift from the code.

## Pages & API

Public — no login:

| Route | What it does |
|---|---|
| `GET /` | Landing page linking to the demo booking page and admin login |
| `GET /book/{slug}` | The business's booking page: pick service, staff, date → real slots → confirm → booked |
| `GET /healthz` | Liveness + database check |

Admin — cookie login, everything scoped to the admin's own tenant:

| Route | What it does |
|---|---|
| `GET /admin` | Dashboard: today's bookings, counts, reminders sent |
| `GET /admin/bookings` | All bookings with date/status filters; cancel, complete, no-show |
| `GET /admin/services`, `GET /admin/staff` | Manage services (duration, price) and staff |
| `GET /admin/staff/hours/{id}` | Weekly working-hours editor per staff member |

JSON API (the booking page and the API share one engine):

| Endpoint | What it does |
|---|---|
| `GET /api/book/{slug}/slots?serviceId&staffId&date` | Available slots for a day |
| `POST /api/book/{slug}` | Book: `{serviceId, staffId, customerName, customerContact, startUtc}` → 201, or 409 slot taken / 422 outside hours or in the past |
| `POST /api/tenants/{slug}/bookings/{id}/cancel` | Cancel a booking; the slot frees immediately |
| `POST /api/tenants`, `…/services`, `…/staff`, `PUT …/staff/{id}/hours` | Tenant setup |

```bash
curl -s "http://5.222.0.70/slotwise/api/book/lakeside-dental/slots?serviceId=…&staffId=…&date=2026-12-02"
curl -s -X POST http://5.222.0.70/slotwise/api/book/lakeside-dental \
  -H "Content-Type: application/json" \
  -d '{"serviceId":"…","staffId":"…","customerName":"Demo User",
       "customerContact":"demo@example.com","startUtc":"2026-12-02T01:00:00Z"}'
# 201 Created — send the same slot again and you get 409 {"error":"slot taken"}
```

## Run it

With Docker (Postgres on host port 5435, app on 8082):

```bash
docker compose up
# → booking page: http://localhost:8082/book/lakeside-dental
# → admin:        http://localhost:8082/admin  (demo login above)
```

Locally (needs the .NET 10 SDK and a PostgreSQL 16 to point at):

```bash
docker compose up db          # or any Postgres on localhost:5435
dotnet run --project Slotwise # → http://localhost:5055
```

The default connection string in `appsettings.json` matches the compose
database; override it with the `ConnectionStrings__Slotwise` environment
variable. Configuration is environment-driven throughout: the connection
string, `PathBase` (set to `/slotwise` in production, where a reverse
proxy routes the prefix without stripping it and `UsePathBase` makes
every generated URL honour it), and `Reminders__IntervalMinutes` /
`Reminders__WindowHours`.

**Seed data:** on first boot, if the database has no tenants at all, the
app seeds one demo business — **Lakeside Dental**: Checkup (30 min,
S$50) and Cleaning (60 min, S$80); Dr. Chen and Dr. Patel, Mon–Sat
09:00–18:00; the admin login above; and a handful of bookings across
today and tomorrow, including one starting within the reminder window
so the worker has something to stamp on its first pass.

## Tests

```bash
dotnet test
```

15 tests, all against real PostgreSQL — the fixture creates a throwaway
database per run, applies the real migrations (exclusion constraint
included) and drops it afterwards. Set `SLOTWISE_TEST_ADMIN_CONNECTION`
to a connection string whose user can create databases (CI does this
against a `postgres:16` service; `btree_gist` is a trusted extension, so
the database owner suffices). They test the guarantees, not just the
endpoints:

- slot generation excludes overlapping candidates and allows
  back-to-back bookings;
- a direct SQL insert that bypasses the service is rejected by the
  exclusion constraint with 23P01 — and an overlapping *cancelled*
  insert is allowed;
- 10 parallel booking attempts for the same slot: exactly one wins;
- cancelling frees the slot, in the engine and through the admin UI;
- bookings in the past or outside working hours are refused;
- tenant B cannot see tenant A's data, and booking under B with A's
  service id returns NotFound;
- the full public flow (page → slot → confirm → booked) and the admin
  login, driven through the real Razor pages by `WebApplicationFactory`;
- one reminder scan stamps the booking due in an hour and leaves the
  one due in five hours alone.

## Out of scope

Deliberately, for v1: no payments for bookings, no customer accounts,
no email/SMS actually sent (reminders are recorded in-app), no
multi-branch locations, no calendar sync. The trilogy it sits alongside:
[settle](https://github.com/TanSayHuan1999/settle) (double-entry
payments), [relayhook](https://github.com/TanSayHuan1999/relayhook)
(webhook delivery), [pg-lens-mcp](https://github.com/TanSayHuan1999/pg-lens-mcp)
(PostgreSQL explainer for AI agents).

## License

MIT
