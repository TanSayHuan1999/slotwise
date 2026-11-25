# Slotwise — Stage 1 notes (core)

Stage 1 of the plan in `~/workspace/your_files/fourth-showcase-plan.md`:
domain, EF Core persistence, the booking engine, a thin API, and an xUnit
suite that proves the no-double-booking guarantee. No UI yet (stage 2).

## Stack as built

- .NET SDK **10.0.404** (runtime/ASP.NET Core 10.0.15), installed via the
  official dotnet-install script into `~/workspace/tools/dotnet`. The
  preferred channel 10.0 (LTS) worked — no fallback to 8.0 needed.
- ASP.NET Core minimal APIs, EF Core 10.0.15 + Npgsql provider 10.0.4,
  PostgreSQL 16.15. (NuGet's "latest" is the 11.x line targeting net11, so
  all EF/Npgsql packages are pinned to 10.0.x for this net10.0 solution.)
- Solution layout: `Slotwise/` (web project; code in `Domain/`, `Data/`,
  `Services/`, `Api/` folders — no extra class libraries) and
  `Slotwise.Tests/` (xUnit v3). Solution file is `Slotwise.slnx`.

## Architecture choices

- **Multi-tenancy:** `ITenantContext` (scoped, settable) + EF Core global
  query filters on Tenant/Service/StaffMember/WorkingHours/Booking. The
  filters read a `CurrentTenantId` property on the DbContext so EF
  re-evaluates them per query. With no tenant selected the filters fail
  closed (comparisons against NULL match nothing) — except `Tenant`
  itself, which is visible while unscoped so slug resolution and tenant
  creation can work; once scoped, a tenant sees only itself. API routes
  resolve the tenant by slug (one deliberate `IgnoreQueryFilters` lookup)
  and set the context; everything downstream is scoped automatically.
- **Times:** all instants are `timestamptz` (`DateTimeOffset`, UTC).
  Each tenant has a `TimeZoneId` (default `Asia/Singapore`); slot dates
  and working-hours checks are interpreted in tenant-local time and
  converted at the boundary.
- **Typed results, not exceptions:** `BookingService.CreateAsync` returns
  `BookingResult` with outcome Success / SlotTaken / OutsideHours /
  InPast / NotFound. The API maps them to 201 / 409 / 422 / 422 / 404.
- **The reminder query** (`GetUpcomingRemindersAsync`) is the one
  deliberate cross-tenant read: it uses `IgnoreQueryFilters` because the
  stage-2 reminder worker is a background job with no current tenant.
  The reason is commented at the call site.

## The core guarantee — enforced twice

1. **App layer:** inside a transaction, `CreateAsync` validates the
   service/staff (active, under the tenant via query filters), rejects
   past starts, checks `[start, start+duration)` fits a working-hours
   window for that weekday in tenant time, and checks for an overlapping
   Confirmed booking for the staff member.
2. **Database layer:** migration `AddNoOverlapExclusionConstraint` adds,
   via raw SQL:

   ```sql
   CREATE EXTENSION IF NOT EXISTS btree_gist;
   ALTER TABLE "Bookings"
   ADD CONSTRAINT "EX_Bookings_NoOverlap"
   EXCLUDE USING gist (
       "StaffMemberId" WITH =,
       tstzrange("StartUtc", "EndUtc") WITH &&
   )
   WHERE ("Status" = 0);   -- Confirmed only
   ```

   `Booking.Status` is stored as int (0 = Confirmed) so the partial
   `WHERE` is a plain literal. tstzrange's default `[)` bounds match the
   app's half-open intervals, so back-to-back bookings are allowed.
   Under READ COMMITTED two racing transactions can both pass check (1);
   the loser's INSERT then fails with SQLSTATE **23P01**, which the
   service catches (`DbUpdateException` → `PostgresException`) and
   translates to `SlotTaken`.

## Tests — 10/10 passing (`dotnet test`, ~8s)

Real PostgreSQL, per-run database (`slotwise_test_<guid>`, created and
dropped by the fixture, real migrations applied):

| # | Test | Proves |
|---|------|--------|
| 1 | SlotGeneration_ExcludesOverlappingCandidates | 09:00–12:00 window, 60-min service, booking at 10:00–11:00 → only 09:00 and 11:00 slots remain |
| 2 | Create_Success_PersistsWithComputedEnd | EndUtc = start + duration, Status Confirmed persisted |
| 3 | Create_SequentialOverlap_ReturnsSlotTaken_AdjacentAllowed | 10:30 overlap → SlotTaken; 11:00 adjacent → Success |
| 4 | DatabaseConstraint_BlocksDirectOverlappingInsert | Raw SQL insert bypassing the service → PostgresException 23P01; overlapping Cancelled insert OK; adjacent Confirmed insert OK |
| 5 | ParallelRace_ExactlyOneWins | 10 concurrent CreateAsync (separate contexts) for one slot → exactly 1 Success, 9 SlotTaken, exactly 1 Confirmed row afterwards |
| 6 | Cancel_FreesTheSlot | Cancel → slot reappears in GetSlots and is bookable again |
| 7 | Create_InPast_Rejected | → InPast |
| 8 | Create_OutsideWorkingHours_Rejected | Before opening, after closing, and overrun past closing → OutsideHours |
| 9 | TenantIsolation_FiltersAndForeignIds | Tenant B sees none of A's data; booking under B with A's service id → NotFound |
| 10 | ReminderQuery_ReturnsOnlyUpcomingUnremindedConfirmed | Only the due, un-reminded, Confirmed booking is returned |

## API smoke test (curl, against the seeded dev DB)

- `GET /healthz` → `{"status":"ok"}`
- `POST /api/dev/seed` → created "Lakeside Dental" (Checkup 30m,
  Cleaning 60m; Dr. Chen, Dr. Patel; Mon–Sat 09:00–18:00)
- `GET /api/book/lakeside-dental/slots?...&date=2026-12-02` → 35 slots
  for the 30-min Checkup, first 09:00 local (01:00Z)
- `POST /api/book/lakeside-dental` at 09:00 → **201**, EndUtc +30m,
  Status Confirmed; identical re-book → **409** `{"error":"slot taken"}`
- `POST /api/tenants/lakeside-dental/bookings/{id}/cancel` → 200
  Cancelled; the freed slot then booked again → 201

## Environment deviations (sandbox-specific, all worked around)

- **The previous throwaway PostgreSQL was gone** (nothing on :5434).
  Recreated: Ubuntu postgresql-16 16.15 debs extracted under `~/pg16`
  (apt itself was wedged), data dir on the home filesystem (not the
  512 MB /tmp tmpfs). This sandbox blocks `chown` and PostgreSQL refuses
  to run as root, so the server runs inside a user namespace mapped to
  uid 1000 (`unshare --map-user=1000`) on 127.0.0.1:5434, superuser
  `settle`, trust auth. Start command is in the server log header /
  shell history; data persists at `~/pg16/data`.
- **Outbound TCP from .NET processes is hijacked by the sandbox egress
  proxy.** Verified experimentally: a .NET TcpClient to 127.0.0.1 gets
  redirected to the proxy (`RemoteEndPoint` = the proxy, reply bytes
  were the proxy's error text), while Python/psql/curl connect cleanly.
  Two consequences:
  - Npgsql connects via the **PostgreSQL Unix socket**
    (`Host=/home/hatch/pg16/run`) in `appsettings.json` and the test
    fixture. On a normal machine, swap back to `Host=127.0.0.1`.
  - The classic **VSTest testhost channel could never connect**, so the
    test project uses **xUnit v3 on Microsoft.Testing.Platform** (the
    test project is a self-running executable) plus a `global.json`
    `"test": { "runner": "Microsoft.Testing.Platform" }` opt-in so
    plain `dotnet test` works on the .NET 10 SDK.
- `dotnet run` uses the template launch profile: the app listens on
  **http://localhost:5055** by default.
- The dotnet-install script needs `TMPDIR` on a real filesystem (its
  default /tmp is the small tmpfs and extraction overflows it).

## Not in stage 1 (by design)

Razor UI, admin auth, the reminder `BackgroundService` (only its query
exists), Docker/CI/deploy (stage 3).
