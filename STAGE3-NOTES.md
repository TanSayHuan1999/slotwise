# Slotwise — Stage 3 notes (launch)

Stage 3 of the plan in `~/workspace/your_files/fourth-showcase-plan.md`:
Docker packaging, the public GitHub repo, the showcase README, the vps-1
deploy, and the terminal demo video. Tests remain **15/15 green**.

## ⚠️ Bootstrap warning (read first)

The box now runs on **cloud-init v5**. **Do not rebuild vps-1 with v4 or
earlier** — v4 restores a box without Slotwise (and v3 wipes Settle too).
v5 is v4 + Slotwise only: clone to `/srv/slotwise`, server override with
the app on `127.0.0.1:8082`, a Caddy block for `/slotwise`, and a health
gate requiring all three apps. Full runbook: `DEPLOY.md` in this folder
(workspace copy); Settle's and relayhook's runbooks carry the same v5
warning.

## Docker

- `Dockerfile` — multi-stage: `dotnet/sdk:10.0` restores + publishes the
  web project, `dotnet/aspnet:10.0` runtime serves it as the `app` user
  on port 8080. (Docker is unavailable on the build VM, so the image was
  first built by the server during the cloud-init deploy and verified
  there — see Deploy.)
- `docker-compose.yml` — local dev: app on host **8082**, `postgres:16`
  db on host **5435**, app reaches the db over TCP via
  `ConnectionStrings__Slotwise` (`Host=db`), `PathBase` deliberately
  unset (the app serves at the root locally).
- `docker-compose.server.yml` — the server override pattern from the
  sister projects: db port unpublished (`ports: !reset []`), app bound
  to `127.0.0.1:8082` only (one-line `ports: !override [...]` — the
  two-line form silently drops the mapping), `PathBase=/slotwise`, DB
  password injected at deploy time. The real file is generated on the
  server by cloud-init with a random password (chmod 600, exists nowhere
  else); the workspace copy is a `${SLOTWISE_DB_PASSWORD}` template and
  is gitignored, like the sister projects' server files.

## Test fixture → CI

`TestDbFixture` hardcoded this dev sandbox's PostgreSQL Unix socket
(`/home/hatch/pg16/run`) — correct locally (the sandbox hijacks .NET TCP
egress), nonexistent anywhere else. It now reads
**`SLOTWISE_TEST_ADMIN_CONNECTION`** when set and falls back to the
socket otherwise; the per-run database connection is derived from the
admin connection with `NpgsqlConnectionStringBuilder` (Database swapped
to the throwaway name). Local `dotnet test` re-verified green (15/15)
after the change. The TCP/env path can't be exercised from this sandbox
(the hijack is why the socket default exists), but it's a pure
connection-string substitution — the admin string is used verbatim for
create/drop, and stage-2 tests already took the app connection from the
fixture.

CI workflow prepared at **`~/workspace/slotwise-ci.yml`** (destination
`.github/workflows/ci.yml`, added via the GitHub web UI afterwards —
the gh token lacks the `workflow` scope, the same constraint as the
sister projects): push/PR to main → `postgres:16` service with health
checks (superuser `postgres`, so CREATE DATABASE and the trusted
`btree_gist` extension work) → setup-dotnet 10.x → `dotnet build` →
`dotnet test`, with `SLOTWISE_TEST_ADMIN_CONNECTION` pointing at the
service on localhost:5432.

Also fixed for the public repo: `appsettings.json` shipped with the
sandbox socket as its default connection string. The default is now the
conventional local-dev TCP string matching the compose database
(`Host=localhost;Port=5435;Database=slotwise;Username=slotwise`), so
`docker compose up db` + `dotnet run` works for a fresh clone; the
sandbox keeps working via the `ConnectionStrings__Slotwise` env
override (used for the pre-deploy smoke test below).

## GitHub

- Repo: https://github.com/TanSayHuan1999/slotwise (public, main),
  description "Slotwise — multi-tenant appointment booking in ASP.NET
  Core + EF Core + PostgreSQL. The database itself prevents
  double-booking.", topics: dotnet, aspnet-core, ef-core, postgresql,
  booking, multi-tenancy, background-service. MIT LICENSE added
  (GitHub's license API confirms MIT).
- Pushed WITHOUT any `.github/workflows` content (token scope, above).
- Showcase `README.md` modelled on Settle's: the double-booking story,
  the exclusion-constraint SQL, the 23P01 → SlotTaken translation, the
  race test, tenant filters, the reminder worker, pages & API tables,
  run/test instructions, out-of-scope list. No demo-video section yet —
  the video goes in only after the owner reviews it (standing rule).

## Deploy (vps-1, 2026-11-26)

- `cloud-init-vps1-v5.yaml` (workspace copy in this folder) = v4 +
  Slotwise. Rebuild action `670169524239491` succeeded;
  `/setup/status.json` reached `done` (v5's gate requires relayhook,
  Settle AND Slotwise healthy on 8080/8081/8082).
- **Routing subtlety, as briefed:** Settle/relayhook strip their prefix
  (`handle_path`); Slotwise must NOT be stripped — it's an ASP.NET app
  with `PathBase=/slotwise` whose tag helpers build URLs from the
  request PathBase. Caddy uses `@slotwise path /slotwise /slotwise/*` +
  `handle` → `127.0.0.1:8082`. A pre-deploy local smoke test (fresh DB,
  `PathBase=/slotwise`) and the live checks below both confirm generated
  URLs carry the prefix.
- Live verification, all passed:
  - `GET /slotwise/healthz` → `{"status":"ok"}`; bare `/slotwise` → 200.
  - Booking page → 200, **35 slot links, all prefixed `/slotwise/…`**;
    admin login page → 200 with its password form.
  - API under the prefix: book Dr. Chen, Checkup, Sat 2026-11-28 09:00
    SGT → **201 Confirmed**; identical re-book → **409
    `{"error":"slot taken"}`** — the exclusion constraint is live on the
    server's Postgres.
  - Neighbours untouched: relayhook `/healthz` `{"ok":true}`; Settle
    `/settle/healthz` `{"ok":true}`, `/settle/verify` `ok:true`.
  - On-box: `slotwise-app-1` published `127.0.0.1:8082->8080` only;
    `slotwise-db-1` healthy, 5432 internal only.
- One "Stage3 Verify" booking (the 09:00 slot above) was left in the
  live demo data deliberately — same call as Settle's "Stage3 Check"
  accounts.

## Demo video (delivered, NOT embedded)

`slotwise-demo.mp4` in this folder — 26.5 s, 1320×924, H.264,
~450 KB, recorded with the sister pipeline (`demo-session.sh` under
`script` → `render-slotwise.py`, adapted dwell/trim markers) against
the LIVE server: healthz → free slots for Lakeside Dental (JSON) →
book 09:30 → same slot again → **409** → cancel → slots again with
09:30 back. QA frames eyeballed at 3/12/20/29 s: every beat legible,
only seeded demo names ("Demo User", "Second User", Lakeside Dental).
Note the demo books 09:30, not 09:00 — the stage-3 verification booking
holds 09:00, which is itself a neat proof the slot engine works.
Embedding waits for the owner's review, per the standing rule.

## Deviations from the brief

- Docker is unavailable on this VM (`docker: command not found`), as
  the brief anticipated: no local image build; the server-side build in
  cloud-init was the first build, verified via `/setup/diag.txt`
  (compose ps: app up, db healthy) plus the live checks above.
- `docker-compose.server.yml` is a workspace template, not a tracked
  repo file — that IS the sister-project pattern (Settle's exists only
  on the server). The gitignored workspace copy documents the shape;
  cloud-init generates the real one.
- Video is 26.5 s rather than ~30 s (the renderer's estimate counts the
  final frame's dwell twice; the concat encode is the truth). Within
  the intended half-minute shape; not re-recorded for 3.5 s.
