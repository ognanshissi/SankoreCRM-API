# SankoreCRM — Deployment runbook (Dokploy)

Verified against this repository on 2026-09-30, and revised on 2026-10-16 for every commit up to
`f191067` (see §13 for what changed). Every image in §0 was built locally from the Dockerfiles
committed here before this document was written; every environment variable below was traced to
the line of code that reads it. No Dockerfile, compose file or Aspire host changed in the revision.

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

On the compose route the registry is a **separate stack** — `docker-compose.admin.yml` plus
`.env.admin`, deployable and restartable on its own, see §3. `docker-compose.yml` carries
everything else.
| **Sankore.Hangfire** | `src/Bootstrapper/Sankore.Hangfire/Dockerfile` | job dashboard **only** (no job server), same storage as the API | restricted |

| Managed service | Required? | Why |
|---|---|---|
| **PostgreSQL 16+** | yes | two databases: the CRM and the tenant registry |
| **Redis** | **yes** | `builder.AddRedisDistributedCache("redis")`; the tenant cache, lead dispatch and the email-provider resolver all inject `IDistributedCache` |
| **RabbitMQ** | no (see §7) | only if you move off the in-memory transport |
| **Seq** | no | `builder.AddSeqEndpoint("seq")` is skipped when `ConnectionStrings__seq` is absent |
| **clamd** (ClamAV daemon) | only if you distribute insurance | scans claim pieces before they are sent to an insurer; without it **nothing is transmitted** (§4) |

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
openssl rand -base64 32   # → Kyc__FieldEncryptionKey       (M02 document numbers at rest)
openssl rand -base64 32   # → Kyc__BlindIndexKey            (M02 duplicate-document lookups)
openssl rand -base64 32   # → Kyc__Storage__EncryptionKey   (M02 identity-document IMAGES)
openssl rand -base64 32   # → Administration__FieldEncryptionKey (M12 professional-card numbers)
openssl rand -base64 32   # → Administration__BlindIndexKey      (M12 lookups on those numbers)
openssl rand -base64 32   # → Integration__FieldEncryptionKey    (integration command payloads)
openssl rand -base64 32   # → Integration__BlindIndexKey
openssl rand -base64 32   # → Integration__Batch__Storage__EncryptionKey          (outbound CBS batch files)
openssl rand -base64 32   # → Integration__Insurance__ClaimStorage__EncryptionKey (insurance claim pieces)
openssl rand -base64 48   # → Jwt__SigningKey               (any string ≥ 32 bytes)
openssl rand -hex 32      # → ApiKey / TenantStore__ApiKey   (the SAME value on both apps)
openssl rand -base64 24   # → Hangfire__Dashboard__Password
```

Every base64 value above is an **encryption key, not a setting**. Losing `Secrets__EncryptionKey` makes every
stored SMTP password and provider API key permanently undecryptable; losing a `Customers__*` key
does the same to client PII, and losing a `Kyc__*` key does it to the KYC evidence a regulator
can demand — `Kyc__Storage__EncryptionKey` in particular is the only thing that can turn the
stored identity-document images back into images. Put them in whatever vault you already trust and
back them up before the first deploy, not after. Rotating one is a data-migration project, so
treat the first value you generate as permanent.

The three `Kyc__*` keys are deliberately distinct from the `Customers__*` ones and from each
other: a KYC document number and a client's phone must not be decryptable with the same key, the
column key and the image key protect data with different lifetimes, and each pair must be
rotatable without the other. Generating one value and pasting it into all three defeats the point
of having three. The same reasoning applies to the `Administration__*` and `Integration__*`
keys: one compromised key must open one concern, so generate each separately.

`Jwt__SigningKey` now protects more than the platform's own tokens. Two further secrets are
**derived** from it with HKDF rather than configured: the signing key of the customer's remote-KYC
session, and the per-tenant token in the Brevo SMS delivery-callback URL. Rotating
`Jwt__SigningKey` therefore also ends every remote-KYC session in progress and changes every
tenant's Brevo callback URL, which then has to be pasted into Brevo again. Set
`Kyc__RemoteSession__SigningKey` (base64, ≥ 32 bytes) if you want the remote sessions rotated
independently.

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

### The compose route: `docker-compose.admin.yml`

Added 2026-10-18. The registry and its Postgres are a **stack of their own**, so they deploy,
redeploy and restart without touching the API, Hangfire, the broker or the observability tools —
two lifecycles, two stacks: the registry changes when a client is onboarded, the API on every
functional release.

```bash
docker network create sankore-shared    # once per host, see "the two stacks talk" below
cp .env.admin.example .env.admin        # fill it in, see below
docker compose -f docker-compose.admin.yml --env-file .env.admin up -d --build
docker compose -f docker-compose.admin.yml --env-file .env.admin logs -f sankore-admin
```

In Dokploy that is a second **Compose** application in the same project, pointed at
`docker-compose.admin.yml`, with `.env.admin`'s contents in its Environment tab. The main stack
keeps `docker-compose.yml`, which no longer declares either service.

**How the two stacks talk.** `TENANTSTORE_BASEURL=http://sankore-admin:8080/` is unchanged:
Compose registers each service name as a DNS alias on every network the container joins, so the
name resolves **across Compose projects** as long as both are on a common network. That network
is `sankore-shared` — external, owned by neither stack (the one that created it would destroy it
on `down` and take the other's link with it), and carrying exactly three containers:
`sankore-admin`, `sankore-api`, and `env-dashboard` so the registry can still push telemetry.

Not `dokploy-network`, though both stacks are already on it: that one carries **everything**
Dokploy runs on the host, so the tenant registry would be reachable from any other application
on the machine, with its API key as the only guard. It has no domain and no reason to be visible
to Traefik. Create the bus once per host — a missing external network fails the `up` by name,
which is the right failure direction:

```bash
docker network create sankore-shared
```

Change `TENANTSTORE_BASEURL` to a public URL only if the registry moves to another host — and
then give it a domain with basic auth in front, following the `env-dashboard` labels in
`docker-compose.yml` (§14). The Dokploy Domains tab alone does not add the middleware.

**`SANKORE_API_KEY` and `ADMIN_POSTGRES_PASSWORD` are declared `${VAR:?...}`**, so Compose
refuses to render the stack at all when either is unset. That is deliberate for the key:
`ApiKeyMiddleware` **skips validation entirely when the configured key is empty**, so a
forgotten variable does not weaken the guard, it removes it — and a stack with its own
environment file is exactly where a variable gets forgotten. The middleware's fail-open
behaviour is unchanged by this split and still worth fixing in code (§9).

**Three values must be identical on both sides**, and nothing fails loudly if they drift:

| `.env.admin` | `.env` (API stack) | What breaks when they differ |
|---|---|---|
| `SANKORE_API_KEY` | `TENANTSTORE_APIKEY` | every API→registry call answers 401 |
| `DEFAULT_TENANT_FQDN` | `DEFAULT_TENANT_FQDN` | `SystemUserSeeder` cannot find the tenant it provisions |
| `DEFAULT_TENANT_ID` | — (registry only) | see below |

**The named volume in the generated stack was never holding the data.** This was found during
the cutover and it is the most important thing on this page. `pgvector/pgvector:pg16` declares
`VOLUME /var/lib/postgresql/data`, and the Aspire-generated compose mounted the named volume one
level up, on `/var/lib/postgresql`. Docker therefore created an **anonymous** volume at
`/var/lib/postgresql/data` which *shadowed* it: the cluster lived in the anonymous volume, and
the named volume held nothing but an empty `data/` directory. A `docker compose down -v`, a
`docker rm -v` or a `docker volume prune` would have destroyed the tenant registry without
warning, and the named volume everyone would have reached for as the backup was decorative.

`docker-compose.admin.yml` mounts `/var/lib/postgresql/data` — PGDATA itself. One line, and the
check is one command: exactly **one** volume, the named one:

```bash
docker inspect sankore-admin-admin-postgres-1 --format '{{range .Mounts}}{{.Name}} -> {{.Destination}}{{"\n"}}{{end}}'
# sankore-admin-postgres-data -> /var/lib/postgresql/data
```

Because of this, the old volume **cannot** be carried over — it contains no cluster, and an
`initdb` refuses a non-empty directory anyway. The data moves by `pg_dump` and restore, which is
the cutover below. `ADMIN_POSTGRES_VOLUME` defaults to a clean `sankore-admin-postgres-data`.

**`DEFAULT_TENANT_ID` is what makes the move survivable.** Pin it before the cutover. The
registry re-seeds the default tenant on a fresh database, and with the id pinned it re-creates it
with *the same* id, so every row of the API's database still resolves. Unpinned, the seeder
generates a new id and the API's data is orphaned behind a tenant that looks identical from the
outside. On this installation the pinned value was already in place, which is the only reason the
first (mis-mounted) attempt was recoverable.

> **The same flaw is still present on the main stack's `postgres` service**, which also mounts
> `sankorecrm.apphost-25fc6f3f86-postgres-data` on `/var/lib/postgresql`. The CRM database —
> clients, leads, KYC, audit — is therefore on an anonymous volume too, and the named volume
> protects nothing. Fixing it is the same one-line change plus a dump/restore of a much larger
> database, so it is left as a decision rather than a side effect of this split: see §9, item 5.

To put the registry on a managed Postgres instead of the bundled container, set
`ADMIN_POSTGRES_HOST` (and `_PORT`, `_USER`, `_DB`) to the internal host Dokploy shows and delete
the `admin-postgres` service; the `depends_on` carries `required: false` for exactly that.

### Cutting an existing single-stack installation over

Performed on this repository on 2026-10-18; the sequence below is the one that worked, including
the two things that went wrong.

The old and the new definitions cannot run at once: they share the container name
`sankore.admin`, the host ports 18200/18433, and the registry's identity. The commands use plain
`docker` for the old pair, because those two services are **no longer in `docker-compose.yml`**
and Compose can no longer address them by service name, while the containers it started are
still running.

```bash
# 0. Build the registry image from current source. Do NOT skip this: the image on this host was
#    eight minutes older than the commit that exempts /health from the API-key middleware, so
#    the new container's probe got 401 and the container never became healthy, while serving
#    traffic perfectly. A stale image is invisible until a health check disagrees with it.
docker compose -f docker-compose.admin.yml --env-file .env.admin build sankore-admin

# 1. What the running registry was told, so the new stack is told the same.
docker ps --filter name=admin --format '{{.Names}}'          # find the old pair
docker exec sankore.admin printenv ApiKey DefaultTenant__Id DefaultTenant__Fqdn \
    DefaultTenant__Name DefaultTenant__RootUserEmail
docker exec <project>-admin-postgres-1 printenv POSTGRES_PASSWORD

# 2. THE dump. This is the migration path, not just a precaution — the named volume holds
#    nothing (see above), so this file is the only thing that moves the data.
docker exec <project>-admin-postgres-1 \
  sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" pg_dump -U postgres -d AdminDatabase' \
  > admin-registry-pre-split-$(date +%F).sql
grep -c '^CREATE TABLE' admin-registry-pre-split-*.sql    # 3: Tenants, TenantDomains, history

# 3. Put the API side on the shared bus FIRST, while the old registry still answers: the API is
#    recreated (a network cannot be attached to a running container) and its SystemUserSeeder
#    needs the registry during that boot.
docker network create sankore-shared
docker compose -f docker-compose.yml up -d sankore-api env-dashboard

# 4. Retire the old pair. No `-v`, ever: it would take the anonymous volume with it, which on
#    the OLD mount layout is where the cluster actually lives.
docker stop sankore.admin <project>-admin-postgres-1
docker rm   sankore.admin <project>-admin-postgres-1

# 5. Fill .env.admin from step 1, then bring up the database ALONE and restore into it before
#    the app can migrate a fresh schema on top of the restore.
docker compose -f docker-compose.admin.yml --env-file .env.admin up -d admin-postgres
until [ "$(docker inspect -f '{{.State.Health.Status}}' sankore-admin-admin-postgres-1)" = healthy ]; do sleep 2; done
docker exec -i sankore-admin-admin-postgres-1 \
  sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" psql -v ON_ERROR_STOP=1 -U postgres -d AdminDatabase' \
  < admin-registry-pre-split-*.sql

# 6. Now the registry. EF finds a complete __EFMigrationsHistory and applies nothing; the
#    seeder finds its tenant and writes nothing.
docker compose -f docker-compose.admin.yml --env-file .env.admin up -d
docker logs sankore.admin 2>&1 | grep -c 'Applying migration'   # must be 0
```

`ON_ERROR_STOP=1` on the restore is deliberate: this dump carries only the `citext` extension and
zero `spatial_ref_sys` rows, so there is nothing to collide with the `init-extensions.sql` the
fresh `initdb` already ran — any error at all means something is wrong and the restore should
stop rather than half-apply.

### Verifying the cutover

```bash
# the registry's own port: answers with the key, refuses without, probe is anonymous
curl -s -o /dev/null -w '%{http_code}\n' -H "X-Api-Key: $SANKORE_API_KEY" http://127.0.0.1:18200/api/v1/tenants   # 200
curl -s -o /dev/null -w '%{http_code}\n'                                   http://127.0.0.1:18200/api/v1/tenants   # 401
curl -s -o /dev/null -w '%{http_code}\n'                                   http://127.0.0.1:18200/health           # 200

# the API reaching it across the two stacks, by service name, over sankore-shared
docker exec sankore.api bash -c 'exec 3<>/dev/tcp/sankore-admin/8080 \
  && printf "GET /health HTTP/1.1\r\nHost: sankore-admin\r\nConnection: close\r\n\r\n" >&3 && head -n1 <&3'

# every tenant is back, with its original id
curl -s -H "X-Api-Key: $SANKORE_API_KEY" http://127.0.0.1:18200/api/v1/tenants
```

Keep the dump and the old volumes until the installation has run for a while. Nothing in the
cutover deletes them: the decorative
`sankorecrm_sankorecrm.apphost-25fc6f3f86-admin-postgres-data` and the orphaned anonymous volume
that holds the pre-cutover cluster are both still on the host, and `docker volume ls -qf
dangling=true` lists the latter among others. That is also the reason not to run a
`docker volume prune` on this host casually.

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

# How long an emailed link stays valid. Optional — shown here as the defaults.
# Activation is long on purpose (the mail may sit unread); reset is short on
# purpose (it is a live credential). A non-positive value fails the boot.
Identity__ActivationTokenLifespan=7.00:00:00
Identity__PasswordResetTokenLifespan=02:00:00

# ── browser origins allowed to call this API (repeat the index per origin) ─
# Without at least one, every cross-origin browser call is blocked and the
# start-up log says so. "*" is refused at boot: credentialed requests forbid it.
Cors__AllowedOrigins__0=https://crm.example.com

# ── inbound service-to-service key; gates POST users/create-root ────────────
ApiKey=<the SAME value as Sankore.Admin's ApiKey>

# ── encryption (see §1 — back these up) ─────────────────────────────────
Secrets__EncryptionKey=<base64 32 bytes>
Customers__FieldEncryptionKey=<base64 32 bytes>
Customers__BlindIndexKey=<base64 32 bytes>
Leads__BlindIndexKey=<base64 ≥ 32 bytes>

# ── KYC (M02) — three SEPARATE keys, see §1 ─────────────────────────────
# Omitting Storage__EncryptionKey does not fail the boot: the document store is
# built on first use, so the symptom is a 500 on the first upload or read with
# "Kyc:Storage:EncryptionKey is not configured". Set it before go-live, not after
# the first agent tries to file a dossier.
Kyc__FieldEncryptionKey=<base64 32 bytes>
Kyc__BlindIndexKey=<base64 32 bytes>
Kyc__Storage__EncryptionKey=<base64 32 bytes>
# The biometry service is not part of this repository. Left empty, face comparison
# answers BIOMETRY_NOT_CONFIGURED without touching the network, and a file stays in
# Verifying instead of being wrongly rejected.
Kyc__Biometry__BaseUrl=
# Optional. Unset, the remote-KYC session key is derived from Jwt__SigningKey (§1).
# Kyc__RemoteSession__SigningKey=<base64 ≥ 32 bytes>

# ── Administration (M12) — professional-card numbers (CIMA art. 510) ────
# Read on first use, not at boot: a missing key breaks the first distribution-
# credential write, not the start-up.
Administration__FieldEncryptionKey=<base64 32 bytes>
Administration__BlindIndexKey=<base64 32 bytes>

# ── Integration — CBS commands, batch files, insurance ──────────────────
Integration__FieldEncryptionKey=<base64 32 bytes>
Integration__BlindIndexKey=<base64 32 bytes>
# Read on the first batch generation; only needed if a tenant's core banking
# system takes its customers in batches.
Integration__Batch__Storage__EncryptionKey=<base64 32 bytes>
# Insurance claim pieces (pièces de sinistre). Read on the first upload: without
# it the boot succeeds and the first upload fails.
Integration__Insurance__ClaimStorage__EncryptionKey=<base64 32 bytes>
# host:port of a clamd. UNSET = fail-closed: every claim piece is recorded
# ScanFailed and NOTHING is transmitted to an insurer. Required for any tenant
# that distributes insurance. Decided once, at start-up: restart after setting it.
Integration__Insurance__Antivirus__Address=<clamd-internal-host>:3310
# Destruction of insurance evidence past its retention period is OFF unless this
# is true (default retention: 10 years, Integration__Insurance__Retention__EvidenceYears).
# Integration__Insurance__Retention__PurgeEnabled=false

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

# ── file storage (must be on the volumes from the next step) ────────────
FileStore__BasePath=/data/imports
Leads__SdkStoragePath=/data/sdk
Kyc__Storage__BasePath=/data/kyc-documents
Integration__Insurance__ClaimStorage__BasePath=/data/insurance-claim-documents
Integration__Batch__Storage__BasePath=/data/integration-batch-files

# ── object storage: Cloudflare R2 instead of the volumes above (optional) ─
# Leave the whole block out and the three paths above are used — which is a
# supported deployment, not a degraded one. Fill it in and KYC evidence and
# import/export files move to buckets; the SDK files do NOT (they are public-read
# and stay on /data/sdk).
#
# One account for the whole installation: these are platform credentials, not
# per-tenant ones, so they live here and not in the secrets vault.
#
# A HALF-FILLED block fails the boot on purpose. Falling back to the filesystem
# because a secret failed to inject would write evidence to a container volume a
# redeploy discards, and nothing would report it.
#
# Create ONE BUCKET PER CONCERN, named exactly:
#   kyc-documents              identity-document scans and selfies (already AES-256-GCM
#                              encrypted by the application BEFORE upload — R2 never sees
#                              an image, and server-side encryption does not replace that)
#   imports                    import spreadsheets and generated client exports
#   insurance-claim-documents  insurance claim pieces (encrypted before upload, like KYC)
#   integration-batch-files    outbound core-banking batch files (encrypted before upload)
# Both must be PRIVATE. The API streams every KYC document through an authenticated,
# audited endpoint; there are no public or presigned URLs, deliberately, because a
# signed link would audit only its issuance and not the read.
# ObjectStorage__R2__AccountId=<cloudflare account id>
# ObjectStorage__R2__AccessKeyId=<R2 access key id>
# ObjectStorage__R2__SecretAccessKey=<R2 secret access key>
# Only for an S3-compatible endpoint that is not R2, or a jurisdiction-specific one.
# ObjectStorage__R2__ServiceUrl=

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
| `/data/kyc-documents` | KYC evidence — identity-document scans and selfies, each AES-256-GCM encrypted | **a redeploy destroys evidence a regulator can demand.** This is the one mount whose absence is not recoverable by re-running something |
| `/data/insurance-claim-documents` | insurance claim pieces, encrypted | same as above for claims: the default is a folder under the content root, which a redeploy destroys (a warning is logged at start-up) |
| `/data/integration-batch-files` | outbound batch files deposited for a core banking system | a batch generated before a redeploy is lost before it is collected; only needed for a CBS that works in batches |

`LocalFileStore` defaults to the container's `/tmp`, so without the first mount an import that
outlives one container is lost. `LocalSdkFileStore` defaults to the image's own
`wwwroot/sdk` — `1.0.0/forms.min.js` ships inside the image and keeps working, but anything
published afterwards does not survive.

With the `ObjectStorage__R2__*` block filled in, every row except `/data/sdk` becomes
unnecessary — those files live in buckets and the mounts hold nothing. `/data/sdk` is still
required either way: the SDK files are public-read and are served from the filesystem on purpose.
Moving an existing installation is covered in §8.

The KYC store falls back to a folder under the content root and **logs a warning saying the
evidence will be written somewhere a redeploy destroys**. It is a warning rather than a refusal so
a dev box works out of the box, which means the only thing standing between a production
deployment and losing an ID-card scan is this mount. Grep the start-up log for
`Kyc:Storage:BasePath is not configured` after the first deploy.

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
  `EmailOutboxProcessor`, `SmsOutboxProcessor`, `SlaCheckerJob` and
  `WorkflowScheduleTriggerJob`) with no leader election, so messages get processed twice — for
  SMS that means a customer receives, and the tenant pays for, every text message twice;
- with `Messaging__UseRabbitMq=false`, publish integration events to an in-memory bus that the
  other replica cannot see, so consumers fire on whichever instance happened to publish.

Scale vertically here. See §7 before scaling out.

### Public routes the proxy must let through

Everything below is anonymous by design and served by the same container on port 8080; Traefik
needs no extra router. What it does need is to **pass the forwarded headers through untouched**.

| Route | Who calls it | Notes |
|---|---|---|
| `/api/v1/public/kyc/link/*`, `/api/v1/public/kyc/session/*` | the customer's browser, during remote KYC | exempt from tenant resolution; rate-limited by `kyc-public` (30 requests/min per IP, in memory). The session endpoints use their own JWT scheme |
| `POST /api/v1/public/sms-webhooks/twilio` | Twilio delivery callbacks | signature verified over the **external** URL, rebuilt from `X-Forwarded-Proto` and `X-Forwarded-Host`. Strip either and every callback is refused with `403`. Rate limiter `sms-webhooks`, 300/min per IP |
| `POST /api/v1/public/sms-webhooks/brevo?tenant=…&token=…` | Brevo delivery callbacks | Brevo signs nothing; the token is derived from `Jwt__SigningKey` (§1). Copy the exact URL from the tenant's SMS settings screen |
| `/sdk/{version}/forms.min.js`, `/sdk/v{major}/forms.min.js` | customers' own websites | served under the any-origin `sankore-sdk-public` CORS policy, **not** `Cors__AllowedOrigins`. `/sdk/{version}/forms.js` is the readable source, for debugging only |

**The remote-KYC link is built from the tenant's FQDN**:
`https://{tenant fqdn}/kyc/verification#t=…`. It must therefore point at the **front-end** and be
reachable over HTTPS — a browser refuses the camera on plain HTTP, so the capture step never
starts. A tenant registered without an FQDN gets no link at all; the failure is only logged.

The front-end's nginx configuration (`SankoreFront/docker/nginx/default.conf`) defines a log
format without request body or `Referer` for `^~ /api/v1/public/kyc`, because those requests carry a
client number, a date of birth and an SMS code. If you put another proxy in front, keep that
property: do not log request bodies or `Referer` on that prefix.

### SDK versions on the `/data/sdk` volume

`SdkVersionSeeder` registers, at every boot, the SDK builds it finds under `Leads__SdkStoragePath`
and promotes the highest one per major (`/sdk/v1/` now resolves to **1.2.1**). The image ships
`wwwroot/sdk/1.0.0` to `1.2.1`, but with the volume mounted the seeder reads the **volume**:
copy any version directory missing from it, otherwise that version is never registered.

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

## 6. First run — the tenant and its root user provision themselves

Nothing works before a tenant exists in the registry: `TenantResolutionMiddleware` rejects
requests it cannot attribute, and the root-user endpoint checks the registry explicitly.

**Both are now provisioned at start-up from the environment**, so steps 1 and 2 below are a
fallback, not the normal path. Set these in the stack's environment and deploy:

```bash
DEFAULT_TENANT_NAME=MFI Côte d'Ivoire
DEFAULT_TENANT_FQDN=https://crm.example.com   # the FRONT-END'S ORIGIN, scheme included
DEFAULT_TENANT_EMAIL=admin@mfi.ci
DEFAULT_TENANT_ID=                            # empty on the first deploy, then pin it
SYSTEM_USER_PASSWORD=<strong>                 # empty disables the whole thing
```

`Sankore.Admin` creates the tenant (`TenantSeeder`), then `Sankore.Api` sends the same
`RegisterCommand` that `create-root` sends (`SystemUserSeeder`). Two lines to look for after a
deploy:

```
[INF] TenantSeeder: provisioned tenant 0f3e… (MFI Côte d'Ivoire) for https://crm.example.com
[INF] SystemUserSeeder: created system user 7a21… (admin@mfi.ci) for tenant 0f3e…
```

Both are idempotent and neither can stop a host from starting. A redeploy logs
`already provisioned` and writes nothing; a failure logs the reason and the manual remedy, and
the host finishes booting — an API that refused to start would not even answer `/health`, which
in Dokploy is a restart loop rather than a diagnosable deployment.

> **`DEFAULT_TENANT_FQDN` is an origin, scheme included — and it is the FRONT-END'S origin, not
> the API's.** Clients stamp every call with `location.origin` (`tenant.interceptor.ts`), and
> `GetTenantByFqdnHandler` compares that against `tenants.fqdn` as a whole string. A bare host
> (`api.example.com`) or a copied URL with a trailing slash is never matched, the deployment
> comes up clean, and every request then answers
> `400 {"error":"Cannot determine tenant…"}` with nothing wrong in any log. The seeders refuse a
> value that is not an origin rather than store one that cannot work.

> **Pin `DEFAULT_TENANT_ID` once, from the first deploy's logs.** Every row of the API's
> database carries that id. A registry rebuilt without it generates a fresh one, and the whole
> history becomes invisible to a tenant that looks identical from the outside. Leave it empty
> the first time, read the id from the line above (or `GET /api/v1/tenants`), then set it. An id
> that matches no existing tenant is ignored with a warning, never forced — and once it is
> pinned, changing `DEFAULT_TENANT_FQDN` *moves* that tenant to the new origin instead of
> creating a second one beside it.

The system user can log in immediately: `LoginHandler` refuses only `UserAccountType.Service`,
does not act on `MfaEnabled`, and enforces no password expiry. It holds every permission,
through the `System` role `RoleSeeder` grants them all to.

### Provisioning by hand

Still the way in for a second tenant, and the fallback whenever a seeder reports it gave up.

**1 — create the tenant** (against Sankore.Admin, with the API key):

```bash
curl -X POST https://admin.example.com/api/v1/tenants \
  -H "X-Api-Key: $API_KEY" -H "Content-Type: application/json" \
  -d '{"name":"MFI Côte d'\''Ivoire","rootUserEmail":"admin@mfi.ci","fqdn":"https://crm.example.com"}'
# → 201 {"id":"<tenantId>"}
```

The `fqdn` is how the API will recognise the tenant, and it must be the FRONT-END'S ORIGIN,
scheme included — `https://crm.example.com`. Not a bare host, and not the API's host: the
comparison is made against the whole string clients send, which is their `location.origin`. This
line used to recommend `api.example.com`; that value never matches.

**2 — create the root user** (against the API). The tenant comes from the request's host or the
`X-Tenant-Fqdn` header, never from a body field:

```bash
curl -X POST https://api.example.com/api/v1/users/create-root \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: $API_KEY" \
  -H "X-Tenant-Fqdn: https://crm.example.com" \
  -d '{"email":"admin@mfi.ci","password":"<strong>","confirmPassword":"<strong>",
       "firstName":"Awa","lastName":"Ouattara","tenantId":"<tenantId>"}'
```

This endpoint carries no JWT — it is what creates the first account — so it is gated by the
`X-Api-Key` header instead, matched against `ApiKey`. Outside Development it is **refused with
`503`** when no key is configured, so a deployment that forgot the key cannot leave super-user
provisioning open. It also refuses a second system user per tenant, and is rate-limited to the
`auth` policy.

Expected outcomes, verified at runtime:

| Call | Response |
|---|---|
| no `X-Api-Key` | `401 {"error":"A valid X-Api-Key header is required."}` |
| wrong key | `401` — same body, so the header's presence is not confirmed to a prober |
| correct key, tenant already has a root | `400 "A tenant could not have more thant one system user."` |
| correct key, no root yet | `201` + the user id |

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

**5 — configure SMS for the tenant** (optional, but required for the remote-KYC one-time code).
`Settings → Notifications → SMS` in the CRM: provider (`Brevo` or `Twilio`), sender identity,
default calling code (e.g. `+225`, needed to convert locally stored numbers), monthly quota and the
credential. The credential goes to the vault under its own name, separate from the email one.
There is **no platform fallback for SMS**: no provider means no text message. Paste the callback
URL the screen shows into the provider's console.

**6 — review the tenant settings that ship with conservative defaults.** None of them needs
changing to boot, but each one changes behaviour:

| Setting | Default | Effect |
|---|---|---|
| Compliance: PEP vigilance | on | turned on for every tenant by migration |
| Compliance: KYC automatic approval | off | no remote file is approved without a human until switched on |
| KYC `remote-otp` | off | cannot be switched on until the tenant's SMS channel can send |
| KYC `remote-email-delay-minutes` | 5 | delay between the welcome email and the remote-KYC link |
| KYC `remote-review-sla-hours` | 24 | when an unreviewed remote file is chased |
| KYC `proof-of-address-max-age-days` | 90 | CIMA 13.6 window for remote files |
| Leads `sla-compliance-target-percent` | 90 | the objective shown on the leads dashboard |
| Product lines: remote-KYC policy | empty | remote collection is off for a product until configured |

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

The Data Protection key ring is no longer a third blocker: it lives in `dataprotection.keys`
under a fixed application name, so every replica unprotects the same activation and
password-reset tokens. Nothing to configure — but the schema has to be reachable, which it is
since the API migrates it at start-up.

---

## 8. Redeploys, migrations and rollback

Every deploy runs migrations at start-up, in this order: the audit schema, the Data Protection key
ring, the secrets vault, then Leads, Administration, Workflow, Notifications, Customers, Kyc and
Integration. A failing migration takes the
boot down, so Swarm keeps the previous task serving traffic until the new one is healthy — which
is exactly what you want, and why the health check must point at `/health`.

Because migrations run in-process, **a rollback of the application does not roll back the
schema.** Redeploying an older commit against a migrated database works only where the older
code tolerates the newer schema. Take a dump before any deploy that carries a migration:

```bash
pg_dump --format=custom -h <pg-host> -U <user> -d <db> > pre-deploy-$(date +%F).dump
```

**The 2026-10-16 revision carries 25 migrations — take that dump.** Three of them rewrite data and
cannot be undone by their `Down` method:

| Migration | Module | What it changes |
|---|---|---|
| `20261012162634_AddMyProfile` | Administration | backfills `app_users.created_at` |
| `20261012235056_UserInvitationAndImpersonation` | Administration | assigns an `AGT-nnnn` employee number to every user without one |
| `20261012225139_BackfillKycInProgress` | Customers | sets `kyc_status = 'InProgress'` on clients that have an open KYC file (no-op on a fresh database) |

The others only add tables and columns: user sessions, agent settings, distribution credentials,
compliance switches, the KYC remote-collection tables, SMS settings and outbox, the insurance
complaint register and purge trace, workflow step kinds, and an impersonator column on the audit
trail. The Leads module has none in this revision.

`docs/backup-runbook.md` has the scheduled, encrypted version of this, and
`docs/postgres-recovery.md` the restore drill. Point the backup container at the Dokploy
Postgres and both apply unchanged.

### Moving existing files to R2

Only needed when an installation already running on the volumes adopts `ObjectStorage__R2__*`.
A fresh install has nothing to move.

The object key a bucket holds **is** the path the volume held — `LocalObjectBackend` writes keys
as relative paths and `R2ObjectBackend` sends the same string as a key. That is deliberate, and it
is what makes this a copy rather than a conversion: an object written by one backend is found by
the other, so a half-finished move still serves every document from whichever side holds it.

```bash
# One bucket per concern, same names as the volumes' contents.
rclone copy /data/kyc-documents r2:kyc-documents --exclude "*.tmp" --checksum --progress
rclone copy /data/imports       r2:imports       --exclude "*.tmp" --checksum --progress
```

`--exclude "*.tmp"` matters: a `.tmp` file is an interrupted write, never an object, and copying
one puts a key in the bucket that no read resolves.

Then set the `ObjectStorage__R2__*` variables and redeploy. **Keep the volumes mounted until you
have read a document back through the API** — the AES-256-GCM key (`Kyc__Storage__EncryptionKey`)
is unchanged by the move, so a document that fails to open after it is a copy problem, and the
volume is the only remaining copy. Unmount them on the deploy after that.

#### Or do it through the API, which audits it

```bash
curl -X POST https://api.example.com/api/v1/object-storage/kyc-documents/migration \
     -H "Authorization: Bearer <JWT of the tenant's System account>"
# → 202 {"concern":"kyc-documents","jobId":"91","sourceBasePath":"/data/kyc-documents",
#        "destination":"R2ObjectBackend"}
```

Repeat with `imports`. The concerns are the only accepted values; there is no path parameter, so
the endpoint cannot be pointed at an arbitrary folder.

This runs `ObjectStoreMigrator` as a Hangfire job, which adds what `rclone` cannot: it re-reads
every object from the bucket and compares it byte for byte before counting it as copied, and it
writes the outcome to the audit trail — `ObjectStorageMigrationCompleted`, with the counts and the
first 50 failing keys. Two audit rows exist per migration: the request, and the result. Query the
second one before deciding the volume may go.

It is resumable: a key already in the bucket is skipped, one failure is recorded and the run
continues, and running it twice is safe. Follow the job in `/hangfire`.

**It never deletes the source**, by construction rather than by configuration — there is no flag to
pass. Retiring the volume is the unmount, after you have read a document back.

Requirements, each of which answers with an explicit error rather than a misleading success:

| Condition | Response |
|---|---|
| Caller is not the **System** account | `OBJECT_STORAGE_MIGRATION_REQUIRES_SYSTEM_ROLE` — a tenant Administrator cannot start a copy that walks every tenant's objects, and holding `platform:storage:migrate` is not enough |
| `ObjectStorage__R2__*` not set | `OBJECT_STORAGE_NOT_CONFIGURED` — there would be no destination, and the copy would report every object "already present" |
| The source folder does not exist | `OBJECT_STORAGE_SOURCE_EMPTY` — nothing to migrate, said out loud rather than logged as a clean run |

---

## 9. Pre-production hardening — read before going live

Two blockers that were here when this runbook was first written are now fixed in the repository
(see §12). What remains:

| # | Finding | Where | Consequence |
|---|---|---|---|
| 1 | **No production health endpoint on Hangfire** | `MapDefaultEndpoints()` is Development-only | no readiness signal for the dashboard host. Admin no longer has this problem: it declares its own `/health` like the API, and `ApiKeyMiddleware` exempts `/health` and `/alive` so a container probe can reach it. Before that, its probe got 401 then 404, the container stayed `unhealthy`, and `sankore-api` — which waited on that health — refused to start at all |
| 2 | **Swagger is Development-only** (this one is correct) | `Program.cs` | do not "fix" it by setting `ASPNETCORE_ENVIRONMENT=Development` in production — that would also expose health details and turn on sensitive EF data logging |
| 3 | **`Messaging__UseRabbitMq=true` cannot authenticate** | `cfg.Host(host)` passes a hostname only | a broker with credentials will not connect; see §7 |
| 4 | **`ApiKeyMiddleware` fails open** | it skips validation when the configured `ApiKey` is empty, rather than denying | an unset variable does not weaken the guard on Sankore.Admin, it removes it: the tenant registry answers every caller that can reach it. Deny-by-default would be the fix; it would also mean every local run needs a key, which is why it has not been changed here. Until then the compose guard is the mitigation — `docker-compose.admin.yml` declares `ApiKey: "${SANKORE_API_KEY:?...}"`, so the stack refuses to render without one, including from Dokploy's Environment tab. The Dockerfile-*Application* route earlier in §3 has no such guard: there, an empty `ApiKey` still boots open |
| 5 | **The CRM database is on an anonymous volume** | `postgres` mounts its named volume on `/var/lib/postgresql`, but the image declares `VOLUME /var/lib/postgresql/data` | the named volume `…-postgres-data` is shadowed and empty; the real cluster is an anonymous volume. `docker compose down -v`, `docker rm -v` or `docker volume prune` destroys clients, leads, KYC and audit data, and the volume a restore would reach for contains nothing. Found 2026-10-18 while splitting the registry out, where the same bug was fixed by mounting PGDATA directly (§3). Same one-line fix here, but it needs a dump/restore of the live database, so it is a scheduled migration — not a redeploy |

Also worth knowing, though not blocking:

- `app.UseHttpsRedirection()` is a no-op in this container: with only an HTTP port bound it
  cannot determine a redirect target and logs `Failed to determine the https port for redirect`.
  Traefik already handles the redirect, so this is fine — just don't expect the app to do it.
- Rate limits are per-instance in-memory (`auth`: 5/min/IP). With one replica that is the real
  limit; behind a proxy it counts the *proxy's* IP unless forwarded headers are enabled, which
  is the other reason `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` matters.
- **Administrators can impersonate users.** `RoleSeeder` grants every permission to the System and
  Administrator roles at each boot, so the new `user:impersonate` permission reaches every
  Administrator automatically. An impersonation lasts 30 minutes and is recorded in the audit
  trail with the impersonator. Remove the permission from the role if your institution does not
  want it.
- **Platform tokens are now tied to a session.** A JWT carrying a session id is checked on every
  request, so revoking a session in the CRM logs that device out immediately.
- `Notifications__Smtp__Password` and the API key sit in Dokploy's environment, visible to
  anyone with access to the panel. That is the same exposure as any PaaS; the per-tenant
  credentials are not exposed this way — they live encrypted in the vault.

### Optional hardening: revoke write access to the audit table

`audit.entries` is **already append-only at the database level** — the `ImmutableEntries`
migration installs triggers that raise on `UPDATE`, `DELETE` and `TRUNCATE`, so the protection is
in place whatever the application does and whichever role is connected. Nothing is required of you.

As defence in depth, a DBA may additionally remove the privilege itself, so a mutation is refused
before it ever reaches a trigger:

```sql
REVOKE UPDATE, DELETE ON audit.entries FROM sankore_app;
```

Do not revoke `INSERT`: the application writes audit rows on every mutation, and a pipeline that
cannot record one fails the request it was recording.

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
| `500` on any request that resolves a tenant, log says `No endpoints specified. Ensure a valid connection string was provided in 'ConnectionStrings:redis'` | Redis missing — the app starts fine and only fails on the first cache read, so this looks like a runtime bug rather than a missing service | set `ConnectionStrings__redis` |
| curl works, the browser does not, console says the CORS policy blocked it | the calling origin is not in `Cors__AllowedOrigins`; the start-up log lists what is allowed | add the front-end origin, scheme included, no trailing slash |
| Boot: `Cors:AllowedOrigins contains "*"` | a wildcard was configured; it is incompatible with credentialed requests | list the exact origins |
| Activation or reset link 400s with "expired or already been used" for a link that is neither | either the lifespan is too short for how the link is used (`Identity__ActivationTokenLifespan`), or the deployment predates the persisted Data Protection key ring, which reset the signing keys on every redeploy | raise the lifespan, and redeploy from a commit containing `SankoreDataProtection`; links issued before that deploy must be re-sent |
| Insurance claim pieces stay `ScanFailed`, nothing reaches the insurer | `Integration__Insurance__Antivirus__Address` unset (fail-closed) or clamd unreachable | point it at a running clamd and restart the API — the scanner is chosen at start-up |
| First claim-piece upload fails with a 500 | `Integration__Insurance__ClaimStorage__EncryptionKey` missing — read on first use, not at boot | set it and redeploy |
| Saving a distribution credential (professional card) fails with a 500 | `Administration__FieldEncryptionKey` / `Administration__BlindIndexKey` missing — read on first use | set both and redeploy |
| Every Twilio SMS callback answers `403` | the proxy strips `X-Forwarded-Proto` or `X-Forwarded-Host`, so the signed URL cannot be rebuilt | keep forwarded headers; `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` |
| Brevo SMS delivery statuses stopped arriving after a key rotation | the callback token is derived from `Jwt__SigningKey` | copy the new callback URL from the SMS settings screen into Brevo |
| A converted customer never receives the remote-KYC link | the tenant has no FQDN, the client has no email, or the product's remote policy is off — the link is skipped and only logged | set the tenant FQDN; check the KYC file's « Dépôt à distance » panel, which names the reason |
| The remote-KYC page opens but the camera never starts | the link's host is served over HTTP | serve the front-end over HTTPS on the tenant FQDN |
| `remote-otp` cannot be switched on | the tenant's SMS channel cannot send yet | finish the SMS settings (§6 step 5) first |
| `create-root` returns `503` | `ApiKey` is not configured and the environment is not Development — deliberate: the endpoint provisions a super-user and refuses to run unprotected | set `ApiKey` |

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

---

## 12. Security fixes applied for this deployment

Two findings in the first draft of §9 were blockers rather than advice, so they are fixed in the
repository. Both were verified against a running instance, not only by unit test.

### CORS origins are configuration

`Program.cs` pinned `http://localhost:4222` in source. No environment variable could change it,
so a deployed front-end was blocked by the browser on every call and the only remedy was a
rebuild. Origins now come from `Cors:AllowedOrigins` (`CorsSetup.AddSankoreCors`):

- `http://localhost:4222` stays the **Development** fallback, so local work needs no config;
- a `*` entry is **refused at boot** with the reason — it cannot be combined with the
  credentialed requests this API makes, and the browser's own error points at the browser
  instead of at the setting;
- with no origin configured outside Development the policy allows none, and start-up logs a
  warning saying so. An empty CORS policy is otherwise invisible: every curl succeeds while the
  front-end fails.

Verified on a Production-mode instance with `Cors__AllowedOrigins__0=https://crm.example.com`:

```
OPTIONS /api/v1/auth/login   Origin: https://crm.example.com   → Access-Control-Allow-Origin: https://crm.example.com
OPTIONS /api/v1/auth/login   Origin: https://evil.example.com  → (no Access-Control-Allow-Origin)
Cors__AllowedOrigins__0='*'                                    → refused at boot
```

### `create-root` is behind the service API key

`POST /api/v1/users/create-root` was reachable by anyone who could reach the API from a
recognised tenant host — which, on a public deployment, is anyone. It self-closed after one use
per tenant, so the window was "until you get there first", not "forever"; on a fresh tenant that
is still someone else's super-user.

It now carries `.RequireApiKey()` (`ApiKeyEndpointFilter`), matched against the same `ApiKey`
value the tenant registry uses. It stays `AllowAnonymous` because there is no JWT to present
when creating the first account — the shared secret replaces it.

The filter differs from `ApiKeyMiddleware` on purpose: the middleware skips validation when no
key is configured, because it guards a host that is entirely internal. This filter allows that
**only in Development**, and answers `503` elsewhere. An endpoint that provisions a super-user
has to fail closed.

`ApiKeyValidator` now holds the comparison for both, using
`CryptographicOperations.FixedTimeEquals`. The previous ordinary string comparison returned as
soon as two bytes differed, which leaks the shared-prefix length through response timing.

**The key itself then undid most of that, and that is now fixed too.** `docker-compose.yml` set
`ApiKey` on `sankore-admin` but not on `sankore-api`, so the API fell back to the value in its
`appsettings.json` — a literal `sk-dev-…`, committed to the repository. The gate was closed
against a secret anybody with the repo could read. The compose file now passes
`ApiKey: "${SANKORE_API_KEY}"` to `sankore-api`, and `appsettings.json` ships it empty, which
puts the fail-closed `503` back in charge of a deployment that forgets it.

If your `SANKORE_API_KEY` (or `TENANTSTORE_APIKEY`) still holds that `sk-dev-…` value, it is
public. Generate a new one and set BOTH to it — Admin validates incoming calls against its own
`ApiKey`, and the API sends `TenantStore__ApiKey`, so they have to match:

```bash
openssl rand -hex 32 | sed 's/^/sk-/'
```

Verified end to end (11 unit tests, plus a live instance):

```
POST users/create-root  no header          → 401 {"error":"A valid X-Api-Key header is required."}
POST users/create-root  wrong key          → 401  (identical body — a prober learns nothing)
POST users/create-root  correct key        → 400 "A tenant could not have more thant one system user."
                                                 (the handler ran: the gate passed)
```

Test suites after both changes: Shared.Infrastructure 69, Administration 230, Leads 207,
Notifications 81, Customers 762 — all green. Workflow fails 1 of 33
(`ActivateTemplateHandlerTests.Activates_template_with_steps`: `ExecuteDeleteAsync` is
unsupported by the EF InMemory provider), which is pre-existing and unrelated.

---

## 13. What changed in the 2026-10-16 revision

Operator-facing changes only, from commit `3e2fbcd` to `f191067`. Nothing changed in the images,
the ports or the topology.

**New configuration** (all optional at boot; each fails on first use or fails closed — §4):
`Administration__FieldEncryptionKey`, `Administration__BlindIndexKey`,
`Integration__Insurance__ClaimStorage__*`, `Integration__Insurance__Antivirus__*`,
`Integration__Insurance__Retention__*`, `Kyc__RemoteSession__*`,
`Notifications__SmsOutboxProcessor__*` (batch 20, every 10 s, 2 attempts — defaults are fine). The
`Integration__FieldEncryptionKey`, `Integration__BlindIndexKey` and `Integration__Batch__Storage__*`
keys predate this revision but were missing from this runbook.

**New external service:** clamd, for insurance claim pieces.

**New storage:** `insurance-claim-documents` and `integration-batch-files`, as volumes or buckets.

**New public routes:** remote KYC (`/api/v1/public/kyc/*`), SMS delivery callbacks
(`/api/v1/public/sms-webhooks/*`), and the any-origin CORS policy on the SDK files. See the
"Public routes" part of §4.

**New background work:**
- hosted service `SmsOutboxProcessor`;
- per-tenant Hangfire jobs `kyc-contract-anniversary-*` (02:15 daily), `insurance-policy-sync-*`
  (03:30), `insurance-premium-collection-*` (03:45), `insurance-claim-sync-*` (04:00),
  `insurance-statement-generation-*` (05:00 on the 1st), `insurance-evidence-retention-*` (02:30
  Sundays, purges only if enabled), `insurance-complaint-deadlines-*` (07:00),
  `insurance-claim-document-scan-*` (every 10 minutes);
- global jobs `kyc-remote-invitation-expiry` (02:30 daily) and `kyc-remote-review-sla` (hourly at
  :20);
- new MassTransit consumers in Kyc, Notifications, Leads, Integration and Customers. They run on
  the in-memory bus like the others; nothing to configure.

**New permissions**, granted at boot to System and Administrator: `user:impersonate`,
`user:credential:manage`, `kyc:remote-invite`, `kyc:auto-approval:control`,
`Ins.Statement.Finalise`, `Ins.Complaint.View`, `Ins.Complaint.Handle`. Grant them to other roles
by hand.

**Seeded at boot:** platform SMS templates (`kyc.remote-otp`, `kyc.remote-invitation`, fr and
en) and new email templates. Idempotent.

**25 migrations**, three of them data rewrites — see §8 before deploying.

**Behaviour changes worth announcing to tenants:**
- A KYC complement asked on a remotely collected file now sends the customer a new link limited
  to the pieces the approver listed.
- A failed lead reception (web form, webhook) can be completed and turned into a lead from the
  reception's detail panel, by users holding `lead:source:manage`; each reading of the raw payload
  is audited.
- The KYC file list accepts `mine=true`, used by the « Mes dossiers » view.
- A scheduled-pull lead source can be saved without an endpoint while in Draft or Testing; going
  live without one is refused with `LIVE_SOURCE_ENDPOINT_REQUIRED`.

---

## 14. Ops dashboards behind domains (Traefik labels in the compose stack)

Added 2026-10-18. Until then the five ops dashboards were loopback-only — `TOOLS_BIND_ADDRESS`
is `127.0.0.1` — and the only way in was an SSH tunnel:

```bash
ssh -N -L 18030:127.0.0.1:18030 -L 18341:127.0.0.1:18341 \
       -L 18672:127.0.0.1:18672 -L 18300:127.0.0.1:18300 \
       -L 18888:127.0.0.1:18888 user@dokploy-host
```

That tunnel still works and is still the right way in for anything not listed below. What
changed is that five services now also carry Traefik labels, so Dokploy publishes them on
their own subdomain.

| Domain variable | Suggested host | Service | Container port |
|---|---|---|---|
| `TOOLS_DOMAIN_GRAFANA` | `grafana.ops.example.com` | `grafana` | 3000 |
| `TOOLS_DOMAIN_SEQ` | `seq.ops.example.com` | `seq` | 80 |
| `TOOLS_DOMAIN_HANGFIRE` | `jobs.ops.example.com` | `sankore-hangfire` | `${SANKORE_HANGFIRE_PORT}` |
| `TOOLS_DOMAIN_RABBITMQ` | `mq.ops.example.com` | `rabbitmq` | 15672 |
| `TOOLS_DOMAIN_ASPIRE` | `aspire.ops.example.com` | `env-dashboard` | 18888 |

**maildev is deliberately absent.** It captures outgoing mail, which means activation and
password-reset tokens in cleartext. It stays on the tunnel.

### What makes the route work

Three things, and all three are easy to get wrong:

1. **`dokploy-network` on the service.** Traefik runs in its own container and routes only to
   networks it is attached to. Each of the five now lists `sankore-internal` *and*
   `dokploy-network`.
2. **`traefik.docker.network: dokploy-network`.** Because the containers are on two networks,
   Traefik would otherwise pick an IP itself and can pick the `sankore-internal` one, which it
   cannot reach. The symptom is a domain that answers intermittently.
3. **The router targets the container port, not the published one.** `3000`, never `18030`.

### Before deploying

1. **DNS first.** One `A` record per host, pointing at the Dokploy host. Traefik asks
   Let's Encrypt on the first request to the domain, and a failed validation is cached.
2. **Set `TOOLS_BASICAUTH_USERS`**, or the domains will not answer at all:

   ```bash
   docker run --rm httpd:2.4-alpine htpasswd -nbB ops 'MotDePasseFort'
   ```

   Paste it into `.env` **with every `$` doubled** (`$$2y$$10$$...`) — Compose reads `$2y` as a
   variable otherwise. Verify the value that actually reaches Traefik:

   ```bash
   docker compose -f docker-compose.yml config | grep basicauth.users
   ```

   An empty value makes the middleware invalid, which makes Traefik refuse the router. That is
   the intended failure direction: a forgotten password never results in an open Seq.
3. **Harden the two dashboards that have no login of their own**, now that they are reachable:
   `GRAFANA_ANONYMOUS_ENABLED=false`, and `SEQ_NO_AUTHENTICATION=False` with
   `SEQ_ADMIN_PASSWORD_HASH` (`seqcli hash`). Traefik's basic auth guards the UI, not the Seq
   ingestion API called with a key.
4. The Aspire dashboard additionally requires a browser token, written to its logs at startup:
   `docker compose logs env-dashboard | grep -i token`.

### If you prefer the Dokploy UI

Dokploy's **Domains** tab on the compose service generates equivalent labels itself. Use one
mechanism or the other, not both — two routers with the same `Host()` rule and different names
is undefined behaviour. The labels committed here are the versioned option, and they carry the
basic-auth middleware, which the UI does not add.

### Host port bindings were left in place

The `${TOOLS_BIND_ADDRESS}:...` publications are untouched, so the SSH tunnel remains a working
fallback when a certificate or a Traefik route misbehaves. Remove them only once the domains are
proven.

---

## 15. Reaching Postgres from your machine

Added 2026-10-18. Both instances declare `expose: 5432` plus a **loopback-only** publication,
so they are reachable from the Dokploy host but never from the internet:

| Service | Compose file | Database | Host port | Used by |
|---|---|---|---|---|
| `postgres` | `docker-compose.yml` | `Database` | `${POSTGRES_PUBLISHED_PORT}` (18432) | `sankore-api`, `sankore-hangfire` |
| `admin-postgres` | `docker-compose.admin.yml` | `AdminDatabase` | `${ADMIN_POSTGRES_PUBLISHED_PORT}` (18433) | `sankore-admin` |

The two instances are now in two different stacks (§3), so every `docker compose` command below
needs the `-f` that matches the service — the ports and the tunnel are unchanged.

Ports live in the 18xxx block for the reason the compose header gives: 15432 is taken by other
Postgres stacks. They bind `${TOOLS_BIND_ADDRESS}` — keep it at `127.0.0.1`, or the databases
are published to the server's public interface, where a `-p` binding typically bypasses `ufw`.

### The connection string

Nothing stores it; the compose file assembles it from `POSTGRES_PASSWORD`:

```
Host=postgres;Port=5432;Username=postgres;Password=<POSTGRES_PASSWORD>;Database=Database
```

In Dokploy the password comes from the stack's **Environment** tab, which is the source of
truth — not any local `.env`. To read what a container actually received:

```bash
docker compose -f docker-compose.yml exec sankore-api printenv ConnectionStrings__Database
```

### A psql prompt, without handling the password

The password is already in the container's environment:

```bash
docker compose -f docker-compose.yml exec postgres \
  sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" psql -U postgres -d Database'
```

`PGPASSWORD` is not optional: the services set `POSTGRES_HOST_AUTH_METHOD=scram-sha-256` and
`--auth-local=scram-sha-256`, so there is no trust auth, not even on the local socket.

For the tenant registry, the same command against the other stack:

```bash
docker compose -f docker-compose.admin.yml --env-file .env.admin exec admin-postgres \
  sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" psql -U postgres -d AdminDatabase'
```

Dump the same way:

```bash
docker compose -f docker-compose.yml exec -T postgres \
  sh -c 'PGPASSWORD="$POSTGRES_PASSWORD" pg_dump -U postgres -d Database' > database.sql
```

### A GUI client (DataGrip, pgAdmin)

```bash
ssh -N -L 18432:127.0.0.1:18432 -L 18433:127.0.0.1:18433 user@dokploy-host
```

Then connect to `localhost:18432`, user `postgres`, database `Database`, with the password from
the Environment tab. Unlike a tunnel to a container IP, this survives every redeploy.

### Not a Traefik domain

The §14 labels route HTTP; Postgres speaks its own TCP protocol, so a domain would need a TCP
router on its own entrypoint — and a publicly reachable database is not the goal. The tunnel is
the supported path.

### If the databases are missing

Both services set `POSTGRES_DB` (`Database` and `AdminDatabase`), so `initdb` creates the named
database on a **fresh** volume — and only there: on a volume that is already initialised the key
is ignored, and the schema comes from the app's EF migrations at start-up instead. A `\l` that
lists neither means the app has not started cleanly yet — read its logs before suspecting the
connection.
