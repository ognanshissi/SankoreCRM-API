# SankoreCRM — Deployment runbook (Dokploy)

Verified against this repository on 2026-09-30. Every image in §0 was built locally from the
Dockerfiles committed here before this document was written; every environment variable below
was traced to the line of code that reads it.

Dokploy runs Docker Swarm behind Traefik: each *Application* is a Swarm service built from a
Dockerfile, each *Database* is a managed container with an internal hostname, and Traefik
terminates TLS and forwards plain HTTP to your container port.

---

## 0. What you are deploying

```
                    ┌──────────────── Traefik (Dokploy) ─────────────────┐
                    │  TLS, Let's Encrypt, one domain per application     │
                    └───┬───────────────┬────────────────────┬───────────┘
                        │               │                    │
                 api.example.com  admin.example.com   jobs.example.com
                        │               │                    │
                 ┌──────▼──────┐ ┌──────▼──────┐   ┌─────────▼────────┐
                 │ Sankore.Api │ │Sankore.Admin│   │ Sankore.Hangfire │
                 │  :8080      │ │   :8080     │   │  :8080 dashboard │
                 │  1 replica  │ │             │   │  basic auth      │
                 └──┬───┬───┬──┘ └──────┬──────┘   └────────┬─────────┘
                    │   │   │           │                   │
              ┌─────▼┐ ┌▼──┐ └──────────┼───────────────────┘
              │ PG   │ │Rds│            │   same Postgres instance,
              │ main │ │   │            └─► database "AdminDatabase"
              └──────┘ └───┘
```

| Application | Dockerfile | Purpose | Public? |
|---|---|---|---|
| **Sankore.Api** | `src/Bootstrapper/Sankore.Api/Dockerfile` | the CRM itself: all modules, Hangfire **server**, outbox processors | yes |
| **Sankore.Admin** | `src/Bootstrapper/Sankore.Admin/Dockerfile` | tenant registry; the API calls it on every unauthenticated request | internal only if you can |
| **Sankore.Hangfire** | `src/Bootstrapper/Sankore.Hangfire/Dockerfile` | job dashboard **only** (no job server), same storage as the API | restricted |

| Managed service | Required? | Why |
|---|---|---|
| **PostgreSQL 16+** | yes | two databases: the CRM and the tenant registry |
| **Redis** | **yes** | `builder.AddRedisDistributedCache("redis")`; the tenant cache, lead dispatch and the email-provider resolver all inject `IDistributedCache` |
| **RabbitMQ** | no (see §7) | only if you move off the in-memory transport |
| **Seq** | no | `builder.AddSeqEndpoint("seq")` is skipped when `ConnectionStrings__seq` is absent |

Postgres needs no extension beyond `citext`, which the tenant-registry migration creates itself
(`AlterDatabase().Annotation("Npgsql:PostgresExtension:citext")`). The repo's
`postgres.Dockerfile` adds PostGIS and pgvector — **neither is used**: `Address` and `GeoPoint`
are mapped as plain double columns. Dokploy's stock PostgreSQL image is enough.

---

## 1. Before you touch Dokploy

1. **A domain per application**, with DNS `A` records pointing at the Dokploy host.
2. **Git access** to this repository from Dokploy (deploy key or PAT).
3. **An SMTP account or Brevo key** for the platform's own outgoing mail (§6).
4. Read §9 before going to production. Three items there are code-level, not configuration.

### Generate the secrets first — write them down before creating anything

```bash
openssl rand -base64 32   # → Secrets__EncryptionKey        (secrets vault master key)
openssl rand -base64 32   # → Customers__FieldEncryptionKey (M01 PII at rest)
openssl rand -base64 32   # → Customers__BlindIndexKey      (M01 duplicate lookups)
openssl rand -base64 32   # → Leads__BlindIndexKey          (lead phone dedup)
openssl rand -base64 48   # → Jwt__SigningKey               (any string ≥ 32 bytes)
openssl rand -hex 32      # → ApiKey / TenantStore__ApiKey   (the SAME value on both apps)
openssl rand -base64 24   # → Hangfire__Dashboard__Password
```

Four of these are **encryption keys, not settings**. Losing `Secrets__EncryptionKey` makes every
stored SMTP password and provider API key permanently undecryptable; losing a `Customers__*` key
does the same to client PII. Put them in whatever vault you already trust and back them up
before the first deploy, not after. Rotating one is a data-migration project, so treat the first
value you generate as permanent.

---

## 2. Create the project and its data services

In Dokploy: **Create Project** → `sankore-crm`, then inside it create two databases.

**PostgreSQL** — note the *Internal Connection Host* Dokploy shows on the database page and use
it verbatim in the connection strings below. Do not guess the hostname; Dokploy derives it from
the project and a suffix.

The API and the tenant registry use **two separate databases on that one instance**. Dokploy
creates only the first, so create the second by hand from the database's terminal:

```sql
CREATE DATABASE "AdminDatabase";
```

The name is arbitrary — it just has to match `ConnectionStrings__AdminDatabase` in §4.

**Redis** — no configuration needed, but copy its internal host and password.

Give both services a **persistent volume** if Dokploy has not already; a Swarm service without
one loses its data on redeploy.

---

## 3. Deploy Sankore.Admin (the tenant registry) first

The API queries it at start-up to register per-tenant recurring jobs, so bring it up first.

**Application → Provider: Git** → this repository, branch `main`.
**Build Type: Dockerfile**, Dockerfile path `src/Bootstrapper/Sankore.Admin/Dockerfile`,
build context `.` (the repository root — the Dockerfile copies `Directory.Packages.props` from
there and the build fails without it).

Environment:

```env
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
ConnectionStrings__AdminDatabase=Host=<pg-internal-host>;Port=5432;Database=AdminDatabase;Username=<user>;Password=<pw>
ApiKey=<the openssl rand -hex 32 value>
```

Domain: `admin.example.com` → container port **8080**, HTTPS + Let's Encrypt.

`UseApiKeyAuth()` rejects every request without a matching `X-Api-Key` — **but it skips
validation entirely when `ApiKey` is unset**. An empty value here silently opens the tenant
registry to the internet. Set it.

Migrations run at start-up (`adminDbContext.Database.MigrateAsync()`), so the first boot creates
the schema and the `citext` extension.

---

## 4. Deploy Sankore.Api

Dockerfile path `src/Bootstrapper/Sankore.Api/Dockerfile`, build context `.`.

### Environment

```env
# ── runtime ─────────────────────────────────────────────────────────────
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_FORWARDEDHEADERS_ENABLED=true

# ── data ────────────────────────────────────────────────────────────────
ConnectionStrings__Database=Host=<pg-internal-host>;Port=5432;Database=<db>;Username=<user>;Password=<pw>
ConnectionStrings__redis=<redis-internal-host>:6379,password=<redis-pw>

# ── identity ────────────────────────────────────────────────────────────
Jwt__Issuer=https://api.example.com
Jwt__Audience=sankore-crm-api
Jwt__SigningKey=<≥ 32 bytes>

# ── encryption (see §1 — back these up) ─────────────────────────────────
Secrets__EncryptionKey=<base64 32 bytes>
Customers__FieldEncryptionKey=<base64 32 bytes>
Customers__BlindIndexKey=<base64 32 bytes>
Leads__BlindIndexKey=<base64 ≥ 32 bytes>

# ── tenant registry ─────────────────────────────────────────────────────
TenantStore__BaseUrl=http://<admin-internal-host>:8080/
TenantStore__ApiKey=<the SAME value as Sankore.Admin's ApiKey>

# ── messaging ───────────────────────────────────────────────────────────
Messaging__UseRabbitMq=false

# ── platform outbound mail (fallback when a tenant has no provider) ─────
Notifications__Smtp__Host=<smtp host>
Notifications__Smtp__Port=587
Notifications__Smtp__Username=<user>
Notifications__Smtp__Password=<password>
Notifications__Smtp__UseStartTls=true
Notifications__Smtp__FromEmail=noreply@example.com
Notifications__Smtp__FromName=Sankore

# ── file storage (must be on the volume from the next step) ─────────────
FileStore__BasePath=/data/imports
Leads__SdkStoragePath=/data/sdk

# ── optional telemetry; omit to disable entirely ────────────────────────
# ConnectionStrings__seq=http://<seq-host>:80
# OTEL_EXPORTER_OTLP_ENDPOINT=http://<collector>:4317
```

`TenantStore__BaseUrl` uses Dokploy's internal host, not the public domain: the call is
service-to-service and does not need to leave the overlay network.

### Mounts

| Mount path | Holds | Consequence of skipping it |
|---|---|---|
| `/data/imports` | uploaded client/user/lead import files, read later by the Hangfire job | an import queued before a redeploy fails with `Stored file not found` |
| `/data/sdk` | lead-capture SDK versions published through the API | published SDK versions vanish on redeploy; `/sdk/v1/forms.min.js` 404s for every embedded form in the wild |

`LocalFileStore` defaults to the container's `/tmp`, so without the first mount an import that
outlives one container is lost. `LocalSdkFileStore` defaults to the image's own
`wwwroot/sdk` — `1.0.0/forms.min.js` ships inside the image and keeps working, but anything
published afterwards does not survive.

### Domain and health

Domain `api.example.com` → container port **8080**, HTTPS + Let's Encrypt.

Health check path: **`/health`**. This one is mapped unconditionally in `Program.cs` and is
anonymous and tenant-exempt. Do *not* point a health check at `/alive` or at `/health` on the
other two applications — those come from `MapDefaultEndpoints()`, which only maps them
**in Development**, and will 404 in production.

### Replicas: leave it at 1

Not a performance recommendation — a correctness one. A second replica of this application
would, on every deploy:

- run all seven module migrations concurrently against the same database;
- run a second copy of every outbox processor (`OutboxProcessor<T>` per module, plus
  `EmailOutboxProcessor`, `SlaCheckerJob` and `WorkflowScheduleTriggerJob`) with no leader
  election, so messages get processed twice;
- with `Messaging__UseRabbitMq=false`, publish integration events to an in-memory bus that the
  other replica cannot see, so consumers fire on whichever instance happened to publish.

Scale vertically here. See §7 before scaling out.

---

## 5. Deploy Sankore.Hangfire (dashboard)

Dockerfile path `src/Bootstrapper/Sankore.Hangfire/Dockerfile`, build context `.`.

```env
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
ConnectionStrings__Database=<exactly the same connection string as Sankore.Api>
Hangfire__Dashboard__Username=admin
Hangfire__Dashboard__Password=<the openssl rand -base64 24 value>
```

The identical connection string is what makes it the same Hangfire storage — that is the entire
integration. This host runs **no** job server: it reads, pauses and resumes what `Sankore.Api`
executes.

With no password set the dashboard rejects every request (and logs a warning saying so), which
is the safe failure but looks like a broken deploy.

Domain `jobs.example.com` → port 8080. There is no production health endpoint on this host, so
leave the HTTP health check off or expect it to fail; `/` redirects to `/hangfire`, which
answers `401` until you authenticate.

Restrict this domain — IP allow-list at Traefik, or no public domain at all and reach it over a
tunnel. Basic auth over TLS is the only thing in front of a dashboard that can requeue and
delete jobs.

---

## 6. First run — provision a tenant, then its root user

Nothing works before a tenant exists in the registry: `TenantResolutionMiddleware` rejects
requests it cannot attribute, and the root-user endpoint checks the registry explicitly.

**1 — create the tenant** (against Sankore.Admin, with the API key):

```bash
curl -X POST https://admin.example.com/api/v1/tenants \
  -H "X-Api-Key: $API_KEY" -H "Content-Type: application/json" \
  -d '{"name":"MFI Côte d'\''Ivoire","rootUserEmail":"admin@mfi.ci","fqdn":"api.example.com"}'
# → 201 {"id":"<tenantId>"}
```

The `fqdn` is how the API will recognise the tenant, so it must be the host clients actually
call — `api.example.com` here, or the front-end's domain if that is what reaches the API.

**2 — create the root user** (against the API). The tenant comes from the request's host or the
`X-Tenant-Fqdn` header, never from a body field:

```bash
curl -X POST https://api.example.com/api/v1/users/create-root \
  -H "Content-Type: application/json" \
  -H "X-Tenant-Fqdn: api.example.com" \
  -d '{"email":"admin@mfi.ci","password":"<strong>","confirmPassword":"<strong>",
       "firstName":"Awa","lastName":"Ouattara","tenantId":"<tenantId>"}'
```

This endpoint is **anonymous** and rate-limited to the `auth` policy. It refuses a second system
user per tenant, so it closes itself after this call — but it is open until you make it. Run it
immediately after the first deploy, and read §9.

**3 — log in and verify the wiring:**

```bash
curl -s https://api.example.com/health                     # {"status":"healthy",...}
curl -s -X POST https://api.example.com/api/v1/auth/login \
  -H "Content-Type: application/json" \
  -d '{"email":"admin@mfi.ci","password":"<strong>"}'      # → JWT

curl -s https://api.example.com/api/v1/users/auth-context \
  -H "Authorization: Bearer $JWT"                          # roles + permissions
```

A `401` on permission-gated endpoints right after a first deploy almost always means the role
seeding ran after your token was issued: log in again.

**4 — configure outbound mail for the tenant** (optional; the platform SMTP above is the
fallback). `PUT /api/v1/notification-settings` with `providerType` `Smtp` or `Brevo` and the
credential; it is written to the vault, never to a column. Then
`POST /api/v1/notification-settings/test-send` — it answers **HTTP 200 even on failure**, so
read `delivered`, not the status code. See the "Secrets vault" section of `CLAUDE.md`.

---

## 7. If you need to scale out later

Two things must change together, or events get lost:

1. **A real broker.** Set `Messaging__UseRabbitMq=true` and `Messaging__RabbitMqHost=<host>`.
   Note the current limitation: `cfg.Host(host)` passes a hostname only, so MassTransit uses
   RabbitMQ's default `guest`/`guest` on the default vhost. A Dokploy RabbitMQ with its own
   credentials will not connect until that call accepts a username and password.
2. **One writer for the background work.** Migrations and the outbox processors have to stop
   running on every instance — either split the hosted services into a single worker
   application, or gate them behind a leader lock.

Until both are done, one replica is the supported topology.

---

## 8. Redeploys, migrations and rollback

Every deploy runs migrations at start-up, in this order: the audit schema, the secrets vault,
then Leads, Administration, Workflow, Notifications and Customers. A failing migration takes the
boot down, so Swarm keeps the previous task serving traffic until the new one is healthy — which
is exactly what you want, and why the health check must point at `/health`.

Because migrations run in-process, **a rollback of the application does not roll back the
schema.** Redeploying an older commit against a migrated database works only where the older
code tolerates the newer schema. Take a dump before any deploy that carries a migration:

```bash
pg_dump --format=custom -h <pg-host> -U <user> -d <db> > pre-deploy-$(date +%F).dump
```

`docs/backup-runbook.md` has the scheduled, encrypted version of this, and
`docs/postgres-recovery.md` the restore drill. Point the backup container at the Dokploy
Postgres and both apply unchanged.

---

## 9. Pre-production hardening — read before going live

Four of these are code changes, and none are hypothetical. They are in the repository as it
stands today.

| # | Finding | Where | Consequence |
|---|---|---|---|
| 1 | **CORS is hardcoded to `http://localhost:4222`** | `Program.cs` `AddCors` | the deployed Angular front gets blocked by the browser on every call; no configuration can fix it |
| 2 | **`POST /api/v1/users/create-root` is `AllowAnonymous`** | `RegisterEndpoint` | anyone who reaches the API before you do can claim the system user of a tenant that has none. It self-closes after one use per tenant, which is mitigation, not protection |
| 3 | **No production health endpoint on Admin or Hangfire** | `MapDefaultEndpoints()` is Development-only | no meaningful readiness signal for two of three services |
| 4 | **Swagger is Development-only** (this one is correct) | `Program.cs` | do not "fix" it by setting `ASPNETCORE_ENVIRONMENT=Development` in production — that would also expose `/health` details and turn on sensitive EF data logging |

Also worth knowing, though not blocking:

- `app.UseHttpsRedirection()` is a no-op in this container: with only an HTTP port bound it
  cannot determine a redirect target and logs `Failed to determine the https port for redirect`.
  Traefik already handles the redirect, so this is fine — just don't expect the app to do it.
- Rate limits are per-instance in-memory (`auth`: 5/min/IP). With one replica that is the real
  limit; behind a proxy it counts the *proxy's* IP unless forwarded headers are enabled, which
  is the other reason `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` matters.
- `Notifications__Smtp__Password` and the API key sit in Dokploy's environment, visible to
  anyone with access to the panel. That is the same exposure as any PaaS; the per-tenant
  credentials are not exposed this way — they live encrypted in the vault.

---

## 10. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Build fails: `error NU1015: ... do not have a version specified` | build context is not the repository root, so `Directory.Packages.props` never reached the image | set build context to `.` |
| Build fails with `error CA1716`/`CA1000` in `Sankore.Shared.Kernel` | `Directory.Build.props` turns warnings into errors in Release; the Dockerfiles pass `/p:TreatWarningsAsErrors=false` to compensate | keep those flags (see §11) |
| Boot: `OptionsValidationException: Secrets:EncryptionKey is not configured` | the vault master key is missing | set `Secrets__EncryptionKey` to a base64 32-byte value |
| Boot: `relation "secrets.entries" does not exist` | an old build without the vault's start-up migration | redeploy from a commit that contains `SecretsServiceCollectionExtensions.InitializeAsync` |
| Boot log: `Failed to register recurring Hangfire jobs` | Sankore.Admin unreachable, or another instance holds the lock | check `TenantStore__BaseUrl`/`ApiKey`; the API still serves HTTP, but SLA escalation, nurturing, recycling and scheduled pulls do not run on this instance |
| Every request `400`/`401` with no tenant | the calling host is not a registered `fqdn` | add a tenant domain in Sankore.Admin, or send `X-Tenant-Fqdn` |
| `401` on permission-gated endpoints for a valid user | the token predates role/permission seeding | log in again |
| `ArgumentNullException (Parameter 's')` from `Convert.FromBase64String` | a `Customers__*` or `Leads__BlindIndexKey` value is missing — these are read lazily, so they fail on first use, not at boot | set them and redeploy |
| Import job fails `Stored file not found` | `FileStore__BasePath` is not on a volume | mount it (§4) |
| Emails silently not sent | tenant provider is `Ses`, `Postmark` or `SendGrid` — accepted but routed to `StubEmailSender`, which only logs | use `Smtp` or `Brevo` |
| `429` under normal load | per-instance rate limiter counting the proxy IP | set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` |

---

## 11. Changes made to this repository for Dokploy

Both Dockerfiles for `Sankore.Hangfire` and `Sankore.Admin` could not build at all before this
runbook; only `Sankore.Api` could. Two commits' worth of change, both mirroring what the API's
Dockerfile already did:

1. **Copy `Directory.Packages.props`, `Directory.Build.props` and `global.json`** into the
   restore layer. This repository uses Central Package Management, so every `PackageReference`
   omits its version and restore fails with `NU1015` without that file.
2. **Pass `/p:EnforceCodeStyleInBuild=false /p:TreatWarningsAsErrors=false /p:NoWarn=CS9113`**
   to `dotnet build` and `dotnet publish`, exactly as the API's Dockerfile does.
   `Directory.Build.props` sets `TreatWarningsAsErrors` in Release and `Sankore.Shared.Kernel`
   still carries `CA1000`/`CA1716` findings. Local and CI builds are unaffected.

All three images were then built from a clean context and verified:

```
Api       BUILD OK   598MB
Hangfire  BUILD OK   577MB
Admin     BUILD OK   398MB
```

The API Dockerfile's restore layer still lists only 7 of its 9 project references — `Customers`
and the two `PublicApi` projects are missing. It builds correctly anyway (`COPY . .` and the
implicit restore during `dotnet build` cover them), so this costs layer-cache efficiency on
rebuilds, nothing more. Worth tidying, not worth blocking a deploy on.
