# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Build
dotnet restore
dotnet build

# Run tests — uses EF InMemory + NSubstitute, no external deps required
dotnet test src/Modules/Leads/Sankore.Modules.Leads.Tests
dotnet test src/Modules/Administration/Sankore.Modules.Administration.Tests

# Run a single test class
dotnet test src/Modules/Leads/Sankore.Modules.Leads.Tests --filter "FullyQualifiedName~DispatchLeadHandlerTests"

# Run the API (requires Postgres + secrets configured)
dotnet run --project src/Bootstrapper/Sankore.Api

# Run the Hangfire dashboard (http://localhost:5210/hangfire, basic auth)
dotnet run --project src/Bootstrapper/Sankore.Hangfire

# Emit the OpenAPI document for the front-end client generator. NO database needed: the host
# builds, maps its endpoints, writes the file and exits — migrations, seeders and every background
# worker are skipped, so regenerating a client cannot alter data or send mail.
# Pass an ABSOLUTE path. `dotnet run` resolves a relative one against the PROJECT directory,
# not your shell's, so `../SankoreFront/...` silently lands in
# src/Bootstrapper/SankoreFront/ and the front-end file is never updated — the emitter logs
# the full path it wrote, which is the only clue.
dotnet run --project src/Bootstrapper/Sankore.Api -- \
  --emit-openapi "$(cd .. && pwd)/SankoreFront/swaggers/sankore-crm-api-swagger.json"
# then, in SankoreFront: npm run openapi-generator
# (swaggers/* is gitignored there, so each developer emits their own copy — an endpoint added here
#  stays invisible to the front until somebody runs these two commands.)

# Run via .NET Aspire (auto-provisions Postgres, RabbitMQ, Seq via Docker)
dotnet run --project SankoreCRM.AppHost
```

### First-time local setup
```bash
dotnet user-secrets init --project src/Bootstrapper/Sankore.Api
dotnet user-secrets set "Jwt:SigningKey" "some-dev-only-secret-at-least-32-bytes-long" --project src/Bootstrapper/Sankore.Api
dotnet user-secrets set "ConnectionStrings:Database" "Host=localhost;Port=5432;Database=sankore_crm_dev;Username=sankore_app;Password=devpassword" --project src/Bootstrapper/Sankore.Api

# Customers module (M01) field-level encryption — dev values already sit in
# appsettings.Development.json; set real ones per environment.
dotnet user-secrets set "Customers:FieldEncryptionKey" "$(openssl rand -base64 32)" --project src/Bootstrapper/Sankore.Api
dotnet user-secrets set "Customers:BlindIndexKey" "$(openssl rand -base64 32)" --project src/Bootstrapper/Sankore.Api

# Leads module (M13) field-level encryption — KEYED, see below. Dev values already sit in
# appsettings.Development.json; set real ones per environment. Validated on FIRST USE, not at
# boot, so a missing key breaks the first encrypted write rather than the start-up.
dotnet user-secrets set "Leads:FieldEncryptionKey" "$(openssl rand -base64 32)" --project src/Bootstrapper/Sankore.Api
dotnet user-secrets set "Leads:BlindIndexKey" "$(openssl rand -base64 32)" --project src/Bootstrapper/Sankore.Api

# Secrets vault master key (AES-256-GCM) — protects every per-tenant credential
# (SMTP passwords, Brevo API keys, lead-source secrets). A dev value sits in
# appsettings.Development.json; the API REFUSES TO START without a valid one.
# Losing it makes every stored secret undecryptable — back it up, don't rotate casually.
dotnet user-secrets set "Secrets:EncryptionKey" "$(openssl rand -base64 32)" --project src/Bootstrapper/Sankore.Api

# Hangfire dashboard credentials — it points at the SAME database as the API and
# denies every request when no password is set.
dotnet user-secrets init --project src/Bootstrapper/Sankore.Hangfire
dotnet user-secrets set "Hangfire:Dashboard:Username" "admin" --project src/Bootstrapper/Sankore.Hangfire
dotnet user-secrets set "Hangfire:Dashboard:Password" "some-dev-only-password" --project src/Bootstrapper/Sankore.Hangfire
dotnet user-secrets set "ConnectionStrings:Database" "Host=localhost;Port=5432;Database=sankore_crm_dev;Username=sankore_app;Password=devpassword" --project src/Bootstrapper/Sankore.Hangfire
```

### EF Core migrations (always specify --context to avoid IdentityDbContext ambiguity)
```bash
dotnet ef migrations add <Name> \
  --project src/Modules/Administration/Sankore.Modules.Administration \
  --startup-project src/Bootstrapper/Sankore.Api \
  --context AdministrationDbContext --output-dir Infrastructure/Migrations

dotnet ef migrations add <Name> \
  --project src/Modules/Leads/Sankore.Modules.Leads \
  --startup-project src/Bootstrapper/Sankore.Api \
  --context LeadsDbContext --output-dir Infrastructure/Migrations

dotnet ef migrations add <Name> \
  --project src/Modules/Customers/Sankore.Modules.Customers \
  --startup-project src/Bootstrapper/Sankore.Api \
  --context CustomersDbContext --output-dir Infrastructure/Migrations

dotnet ef database update \
  --project src/Modules/Customers/Sankore.Modules.Customers \
  --startup-project src/Bootstrapper/Sankore.Api --context CustomersDbContext

dotnet ef database update \
  --project src/Modules/Administration/Sankore.Modules.Administration \
  --startup-project src/Bootstrapper/Sankore.Api --context AdministrationDbContext

dotnet ef database update --project src/Modules/Leads/Sankore.Modules.Leads \
  --startup-project src/Bootstrapper/Sankore.Api --context LeadsDbContext
```

### Docker (without Aspire)
```bash
docker-compose up
docker compose exec postgres psql -U sankore_app -d sankore_crm
```

## Architecture

**Modular Monolith + Vertical Slice** on .NET 10. All HTTP routes are prefixed `api/v1`.

```
SankoreCRM.AppHost/           ← Aspire orchestrator (provisions Postgres, RabbitMQ, Seq)
SankoreCRM.ServiceDefaults/   ← Aspire shared defaults (health checks, telemetry)
src/
  Bootstrapper/Sankore.Api/        ← Single host; only project referencing all module main assemblies
  Bootstrapper/Sankore.Hangfire/   ← Hangfire dashboard host (no Hangfire server — see below)
  Shared/Sankore.Shared.Kernel/    ← AggregateRoot, Result<T>, DomainEvent, Address, GeoPoint,
                                      ITenantContext, Permissions, Roles. Zero deps.
  Shared/Sankore.Shared.Infrastructure/ ← MediatR behaviors, Outbox, Auth policies
  Modules/
    Administration/
      Sankore.Modules.Administration.PublicApi/  ← IAdministrationModule contract only
      Sankore.Modules.Administration/            ← Identity, AppUser, Agency, Territory, features
      Sankore.Modules.Administration.Tests/      ← xUnit + FluentAssertions + NSubstitute
    Leads/
      Sankore.Modules.Leads.PublicApi/           ← ILeadsModule contract only
      Sankore.Modules.Leads/                     ← Features/CaptureLead, Features/DispatchLead
      Sankore.Modules.Leads.Tests/
    Customers/                                   ← M01: clients PP/PM, groups, doublons, 360°
      Sankore.Modules.Customers.PublicApi/       ← ICustomersModule + integration events
      Sankore.Modules.Customers/                 ← schema "customers"
      Sankore.Modules.Customers.Tests/
```

### Core rules

**Module isolation:** Modules call each other only via their `*.PublicApi` interface. No module ever references another module's domain, infrastructure, or features directly.

**Vertical slices:** Each feature lives entirely in `Features/<Area>/<FeatureName>/` — Command/Query, Handler, Validator (commands only), Endpoint. Adding a slice touches only that folder plus one `app.Map*();` call in the area's `*Endpoints.cs` aggregator. The aggregator is registered in `AdministrationModule.MapAdministrationModuleEndpoints()`.

**Endpoint grouping pattern:**
```
Features/<Area>/<FeatureName>/<FeatureName>Endpoint.cs   ← individual endpoint
Features/<Area>/<Area>Endpoints.cs                        ← area aggregator (MapGroup + MapXxx calls)
AdministrationModule.cs                                   ← calls app.MapXxxEndpoints() per area
```

**Commands vs Queries:** Commands implement `ICommand` (marker in `Sankore.Shared.Infrastructure.Behaviors`) to activate `TransactionBehavior` + `AuditBehavior`. Queries do NOT implement `ICommand`.

**MediatR pipeline order** (outermost-first, registered in `Program.cs`):
`LoggingBehavior → ValidationBehavior → TransactionBehavior → AuditBehavior → [Handler]`

**Multi-tenancy:** `ITenantContext.CurrentTenantId` from JWT `tenant_id` claim. All DbContexts apply global query filters on tenant-scoped entities. Login endpoint must call `.IgnoreQueryFilters()` since no JWT exists yet.

**Outbox / IEventPublisher:** Keyed service per module (key = DbContext type name). Inject with `[FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher`.

**Authorization:** `AddSankoreAuthorization()` auto-generates one policy per entry in `Permissions.All` (policy name = `permission.Code`, e.g. `"agency:create"`). Add new permissions to `Sankore.Shared.Kernel/Permissions.cs` and include in `Permissions.All`. Endpoints call `.RequireAuthorization("permission:code")`.

**Messaging:** MassTransit over **RabbitMQ by default** — `Messaging:UseRabbitMq` is `true` in both
`appsettings.json` and `appsettings.Development.json`. In-memory is the opt-out (`false`), and it is
what a brokerless `dotnet run` needs: with no broker reachable the bus retries forever, logging
`Connection Failed: rabbitmq://localhost/ ... Connection refused`, while the API keeps answering
HTTP — so the symptom reads as log noise even though no integration event is being delivered.

Where the broker lives depends on how you start the host, and the two are not interchangeable.
Under **Aspire**, `AddRabbitMQ("rabbitmq")` + `WithReference(rmq)` inject
`ConnectionStrings:rabbitmq` (`amqp://guest:<generated>@localhost:<random host port>`): the port is
ephemeral and the password is regenerated per run, so neither can be configured by hand — the
connection string is read first for exactly that reason. `Messaging:RabbitMqHost` is only the
**docker-compose** fallback, where 5672 is mapped and the credentials stay guest/guest.

Both transport branches must call `cfg.ConfigureEndpoints(context)`. Without it no receive endpoint
is bound and the registered consumers never run, with nothing in the logs: publishing to an exchange
that has no queue **succeeds**.

**Background jobs (Hangfire):** `Sankore.Api` owns execution — it calls `AddHangfireServer()` and registers every recurring job at startup. `Sankore.Hangfire` is a separate host that mounts *only* the dashboard (`/hangfire`) against the same Postgres storage, behind HTTP Basic auth (`Hangfire:Dashboard:Username` / `:Password`, denies everything when unset). It references the Leads and Administration assemblies purely so job types resolve for display — it registers none of their services and runs no Hangfire server.

Its extra **Job control** page adds pause/resume, which Hangfire OSS has no equivalent for: `RecurringJobPauseStore` (`Sankore.Shared.Infrastructure/BackgroundJobs/`) snapshots a recurring job's definition into Hangfire's own storage, removes it from the schedule, and records its id in the `sankore:paused-recurring-jobs` set. `Sankore.Api` skips re-registering any id in that set, so a pause survives a restart — without that guard `RecurringJob.AddOrUpdate` would resurrect it on the next boot.

### Administration module specifics

`AdministrationDbContext` extends `IdentityDbContext<AppUser, AppRole, Guid>`. Default schema: `administration`. Migrations history table is in the `identity` schema.

`AdministrationModule.InitializeAsync(sp)` runs migrations + `RoleSeeder` + `PermissionSeeder` at startup. `RoleSeeder` seeds `Roles.All` and grants all permissions to the `System` role.

**AppUser factories:**
- `AppUser.Create(tenantId, agencyId, fullName, email)` — standard user, `AgencyId` required
- `AppUser.CreateRoot(tenantId, firstName, lastName, email)` — super-user, no agency, `IsSuperUser = true`, `AccountType = System`
- `AppUser.CreateAgent(...)` — standard user with dispatching fields

**Agency hierarchy:** `AgencyType` ∈ {HeadQuarter, Branch, ServicePoint, Counter}. Non-HQ agencies require a `ParentAgencyId`. `Agency.Deactivate()` soft-deletes (sets `IsDeleted = true`, `IsActive = false`). Cannot delete an agency that still has users.

**Domain entities in Administration:** `AppUser`, `Agency`, `Territory`, `Permission`, `RolePermission`, `UserProfile`, `PasswordHistory`, `UserLoginLocation`, `PermissionAttribution`, `ProductSpeciality`.

**Roles** (seeded at startup): System, Agent, Administrator, SalesManager, BranchManager, CommercialAgent, Cashier, RegulationManager.

### Shared kernel types

- `AggregateRoot` — base for all aggregate roots; holds `TenantId` + domain event list
- `Result<T>` / `Result` — discriminated union returned by all handlers
- `Address` — owned value object with `Create()` factory; mapped as EF owned entity
- `GeoPoint` — lat/lng value object
- `DomainException` — thrown from domain methods for invariant violations

### Customers module specifics (M01)

`CustomersDbContext` — schema `customers`, migrations history in `customers`. Central
`HasQueryFilter` per tenant-scoped entity, `OutboxMessage` excluded. Contract:
`Sankore.Modules.Customers.PublicApi.ICustomersModule` — the single customer contract, and it
exposes no sensitive field. The legacy `Customer360` module it superseded is gone; nothing
outside this module may reach a client record by any other route.

**`AddFieldProtection` may be called ONCE per container, and it throws on the second call.**
`FieldProtectionOptions` is one options instance and `IFieldEncryptor` one singleton, so a second
section does not give a module its own key — it overwrites the first module's, making everything
already encrypted undecryptable. M01 holds the unkeyed registration; **every other module registers
KEYED**:

| Module | Helper | DI key | Settings |
|--------|--------|--------|----------|
| M01 Customers | `AddFieldProtection(config, "Customers")` | *(unkeyed)* | `Customers:FieldEncryptionKey`, `Customers:BlindIndexKey` |
| M02 KYC | `AddKycFieldProtection` | `KycFieldProtection.Key` | `Kyc:FieldEncryptionKey`, `Kyc:BlindIndexKey`, `Kyc:Storage:EncryptionKey` |
| M13 Leads | `AddLeadsFieldProtection` | `LeadsFieldProtection.Key` | `Leads:FieldEncryptionKey`, `Leads:BlindIndexKey` |

Inject with `[FromKeyedServices(LeadsFieldProtection.Key)] IFieldEncryptor`. M13's pre-existing
`IPhoneBlindIndexer` / `HmacPhoneBlindIndexer` is unrelated and stays: its indexes are already in
`leads.leads.phone_blind_index` and F13.2 duplicate detection reads them, so rebasing them under
another key would silently stop every phone match from resolving.

**Field-level encryption (F12.14)** lives in `Sankore.Shared.Infrastructure/Crypto/`, not in
the module, so M02 can reuse it: `IFieldEncryptor` (AES-256-GCM, payload
`v1:nonce:tag:ciphertext`) and `IBlindIndexer` (HMAC-SHA256 over `purpose:normalizedValue`,
lowercase hex). Register with `services.AddFieldProtection(config, "Customers")`; keys come
from `Customers:FieldEncryptionKey` / `Customers:BlindIndexKey` (base64, 32 bytes — dev
values are in `appsettings.Development.json`, set real ones through user-secrets).

Encrypted: identity-document number, phones, email, postal address, date of birth, declared
income, RCCM, NIF. **Names stay in clear** so prefix search remains an indexable
`LIKE 'TERM%'`; `Client.SearchKey` (upper-case, accent-free, surname first) is maintained by
`SearchKeyBuilder` and MUST be used on both the write and the read side.

`SensitiveValueNormalizer.NormalizePhone` keeps the indicatif. It strips the `00`
international prefix and neutralises the national trunk `0` **only** for countries whose plan
has one (France, Belgium, UK, Ghana, Nigeria, DR Congo…), never for Côte d'Ivoire, Senegal or
Mali where a leading `0` is part of the subscriber number. A number typed locally therefore
does not match the same number typed internationally — deliberate, single point of change.

**Duplicate detection never decrypts.** Blocking checks are equality lookups on a blind index;
the nightly scorer (`ClientMatchScorer`) compares phonetic keys, the date-of-birth blind index,
agency and parents' names, blocking candidates by phonetic key to avoid a cartesian product.
`WestAfricanPhoneticKeyCalculator` folds West-African spelling variants (`ou`/`w`, `dj`/`j`,
`kh`/`k`, doubled letters) before Double Metaphone.

**Agency perimeter.** `IAgencyScopeProvider` (Kernel) is implemented by Administration
(`AgencyScopeProvider`: BFS over the agency tree + active `PermissionAttribution` rows scoped
to an agency); `null` means unrestricted (super-user). `AgencyAuthorizationBehavior` rejects
any command implementing `IAgencyScopedRequest` with `AGENCY_OUT_OF_SCOPE`. A single read
outside the perimeter returns `CLIENT_NOT_FOUND` (404), never 403 — the existence of a client
must not leak.

**Tenant parameters** are module-owned (`customer_settings`, `ICustomerSettings`, defaults in
`CustomerSettingKeys.Defaults`, seeded idempotently by `CustomerSeeder` for every active
tenant) because M12 exposes no generic settings store. Legal forms are a per-tenant closed
list in `legal_forms`.

**Idempotent consumers**: every MassTransit consumer of the module starts with
`IInboxGuard.TryBeginAsync(...)` backed by `inbox_messages`, so a replayed KYC event never
changes state twice. Client status follows M02's decisions; a `KycValidated` on a Suspended or
Archived client updates `KycStatus` only.

**Merge is four-eyes.** `ClientMergeRequest` refuses approval by its own requester
(`SELF_APPROVAL_FORBIDDEN`); an M12 `WorkflowInstance` is created for traceability but the M12
engine enforces no self-approval rule of its own, so M01 owns that guarantee.

Concurrency uses the PostgreSQL `xmin` token (`Version`) with error `CONCURRENCY_CONFLICT`.
No endpoint deletes a client: archiving is a status, anonymisation blanks encrypted fields.

### Lead capture, qualification and dispatching

**Dispatching no longer requires `Qualified`.** `Lead.IsDispatchable` is the single definition,
shared by `Lead.AssignTo` and `DispatchLeadHandler`: any live lead may be dispatched, only the
terminal statuses (Converted, Lost, Disqualified, Archived) refuse — routing a captured lead to
an agent is how it gets qualified. Error code: `LEAD_NOT_DISPATCHABLE`.

**The qualification threshold is configurable**, `Leads:QualificationThreshold`, default 60
unchanged. Do not lower it casually, and do not assume 60 is reachable at capture:
`LeadScoreCalculator` awards 35 of its 100 points from activity history (20 interactions,
15 behaviour), which is empty at capture. Measured ceilings on a freshly captured lead are **60
for a walk-in** (`LeadSource.Agency`, source quality 20/20) and **45 for a file import**
(`LeadSource.FileImport`, 5/20). A complete row of `docs/sample-lead-import.xlsx` scores 30.

**Which rule applies is resolved from the lead**, by `DispatchingRuleResolver`: the rule pinned
on the source the lead was ingested through (`LeadSourceConfig.DefaultDispatchingRuleId`, reached
directly through `Lead.LeadSourceConfigId`), else the highest-priority active rule,
else `DispatchingRule.Default()`. The rule then carries the strategy. A caller that names a
strategy explicitly still gets the rule tuned for it, so the manual dispatch screen is unchanged.
`LeadAssignment.RuleId` records which rule produced an assignment (null = built-in defaults).

**`Lead.LeadSourceConfigId`** is the lead's own reference to the `LeadSourceConfig` it arrived
through. It is **null** for a lead typed into the UI, imported from a file, or produced by a
merge — none of those arrived through a configured source, and null says exactly that. It is
distinct from `Lead.Source` and not derivable from it: that enum is a coarse reporting axis
(`LeadSource`) while a source config carries a different one (`LeadChannelType`) with no mapping
between them, so both are kept. An **opaque reference with no foreign key**, like
`LeadAssignment.RuleId`: an archived source leaves a dangling id and every reader degrades to
null rather than assuming it resolves.

It is **server-set only** and deliberately absent from `CaptureLeadRequest` /
`SystemCaptureLeadRequest`. Nothing validates the id, so a client able to set it could name any
source in the tenant and thereby inherit its dispatching rule — choosing which agent pool
receives its leads, past the administrator's configuration — and bill its `CostPerLead` against
that source in `GET lead-sources/quality`. For the same reason `ReplayIngestionJob` **overwrites**
it from the ingestion row instead of defaulting it: `RawPayloadJson` is remote input and is
deserialized straight into a `CaptureLeadCommand`.

Before it existed the link lived only on `LeadIngestion.SourceId`, so `DispatchingRuleResolver`
joined through that side table on every strategy-less dispatch — including the majority of leads
that have no ingestion row at all. `SourceQualityHandler` still measures through the ingestion
rows **on purpose**: a `Duplicate` ingestion points at the surviving lead while carrying the
incoming source id, so grouping leads by the new column would move leads between sources and
change figures already published to tenants.

`DispatchingRuleSeeder` (run from `LeadsModule.InitializeAsync`) gives every tenant one visible
rule named **"Par défaut"**, carrying exactly `DispatchingRule.Default()`'s values — so the
behaviour is unchanged and only its visibility and traceability are new. It seeds **only for a
tenant that has no rule at all**: a tenant which configured its own has made its choice, and
re-adding a default after a deliberate deletion would be a startup undoing an administrator's
decision. Dispatching still works with an empty table, and a test pins that.

**Auto-dispatch on capture** is off by default (`Leads:AutoDispatchOnCapture`). When on,
`CaptureLeadHandler` publishes `LeadCapturedEvent` **through the outbox**, in the lead's own
transaction, and `LeadAutoDispatchConsumer` scores, qualifies and dispatches out of band. Never
do this inline: a lead must persist when no agent is free, and a 400-row import would otherwise
run 400 synchronous dispatches inside its loop. The consumer skips a lead captured with an owner,
a suspected duplicate, and anything already assigned — its idempotency is `CurrentAssignmentId`,
not an inbox table. It establishes `BackgroundJobContext.SetScope` **before** creating its DI
scope, because `ITenantContext` and `ICurrentUser` are built from it and a consumer has no HTTP
context.

The capture → scoring → qualification → dispatch → conversion walkthrough, with the measured
score of each dimension and the cases a change here must not break, is in
`docs/plan-test-qualification-lead.md`; `docs/workflow-complet-leads.md` lists every endpoint of
the module.

### Language codes

**Every write of a language code goes through `LanguageCode` (`Sankore.Shared.Kernel`)**:
`Normalize` lower-cases and keeps the primary subtag ("FR", "Fr", "fr-FR" → "fr"), falling back
to `LanguageCode.Default` ("fr"); `NormalizeOrNull` applies the same rule but keeps blank as
`null`, for fields where "no preference" means "use the tenant default".

It is in the Kernel because four places must agree and did not: `ScribanTemplateRenderer`,
`Client`, `AppUser` and `UserProfile`. Email template locales are stored lower-case and resolved
in PostgreSQL, where string equality is **case-sensitive**, so a client whose language read "FR"
matched no template — and the renderer's "fr" fallback was itself skipped, its guard comparing
case-INsensitively. Those clients were mailed the message's own JSON payload as its body.
`Client.DefaultLanguage` made it likely by defaulting to `"FR"` upper-case.

The renderer still normalises on read, so existing rows are safe without a migration; the write
side is normalised so the column holds one value per language.

### Timestamps are UTC, enforced at the DbContext

Npgsql maps `DateTimeOffset` to `timestamp with time zone` and **refuses any offset but zero**:
`Cannot write DateTimeOffset with Offset=02:00:00 to PostgreSQL type 'timestamp with time zone',
only offset 0 (UTC) is supported.` It is an `ArgumentException` from inside `SaveChanges`, so it
reaches the client as a 500 on an ordinary write and the message names neither the property nor the
request.

`UtcTimestamps.UseUtcDateTimeOffsets()` (`Shared.Infrastructure/Persistence/`) converts on the way
to the column, and **every DbContext that owns a timestamp calls it from `ConfigureConventions`** —
the six module contexts plus `SecretsDbContext`, `AuditDbContext` and `AdminDbContext`. A new
timestamp column is covered without anybody remembering. `DataProtectionKeysDbContext` is the one
left out: its single framework-owned entity is `(Id, FriendlyName, Xml)`, with no timestamp at all.

Why a convention and not a fix per call site: nothing in this solution produces a local timestamp —
there is no `DateTimeOffset.Now` anywhere, only `UtcNow` and `TimeProvider.GetUtcNow()`. The offset
arrives from OUTSIDE, in a request body carrying `2026-10-10T14:00:00+02:00`, bound onto a command
and written straight to a column. Every endpoint taking a client-supplied instant is such a path —
`LogActivity`'s `ScheduledAt`, `RecordFirstContact`'s `ContactedAt`, `SetConnectionSecret`'s
`ExpiresAt`, `RecordRelayHeartbeat`'s `CheckedAt`, a workflow task's `DueAt` — and so is the next
one added. The conversion is lossless for ordering and comparison (`ToUniversalTime` keeps the
instant); what it drops is the originating offset, which no column here stores anyway.

It changes no migration — the store type is unchanged, and
`dotnet ef migrations has-pending-model-changes` reports nothing for any context. **The test suite
cannot catch a regression through an insert**: EF InMemory accepts any offset happily. The guard is
`UtcTimestampConventionTests`, which asserts on the converted value and walks `db.Model` for a
`DateTimeOffset` property without a converter.

### Reading spreadsheets

All three importers (users M12, clients M01, leads M13) accept CSV and .xlsx. **Read every
.xlsx cell through `XlsxCell.ReadAsText` / `ReadAsTextOrNull`** (`Sankore.Shared.Spreadsheets`),
never `cell.GetString()`.

The import rows are deliberately all-string — a spreadsheet only ever hands back text, and typed
binding would abort a whole file on one bad cell — and the parsers then read those strings with
`InvariantCulture`. `GetString()` renders a TYPED cell with the *process* culture, so the two
disagree: on a fr-FR host a numeric Latitude arrives as `5,300489` and every row is rejected as
"not a number", and a date cell arrives as `02/04/1987`, which the invariant short-date pattern
(MM/dd/yyyy) reads as 4 February instead of 2 April — silently. Both depend on where the process
runs, so they pass every test on a machine whose culture is invariant.

`Sankore.Shared.Spreadsheets` exists to keep ClosedXML off the projects that never open a
workbook: it is referenced by those three modules only, not by `Shared.Infrastructure`, so
`Sankore.Admin` ships no spreadsheet engine.

### Host configuration that must not be hardcoded

**CORS origins** come from `Cors:AllowedOrigins` (a string array), bound by
`CorsSetup.AddSankoreCors` in the API bootstrapper. `http://localhost:4222` is only the
Development fallback. A `*` entry is refused at boot — it is incompatible with the credentialed
requests the API makes. With nothing configured outside Development the policy allows no origin
and start-up logs a warning; an empty CORS policy is otherwise invisible, since every curl keeps
working while the browser blocks the front-end.

**`POST users/create-root`** provisions a tenant's system user and cannot sit behind a JWT, so it
carries `.RequireApiKey()` (`ApiKeyEndpointFilter`) against the root `ApiKey` value — the same
secret the tenant registry uses. Unlike `ApiKeyMiddleware`, which skips validation when no key is
configured, the filter allows that only in Development and answers 503 elsewhere: an endpoint
that creates a super-user fails closed. Both share `ApiKeyValidator.Matches`, which compares in
constant time.

**Swagger is behind HTTP Basic auth** on both hosts (`Sankore.Api`, `Sankore.Admin`).
`SwaggerExposure.IsEnabled` decides whether `/swagger` is mapped at all — `Swagger:Enabled` when
set, otherwise Development only, which is the behaviour the inline `IsDevelopment()` checks had — and
`UseSwaggerBasicAuth()` (`Shared.Infrastructure/Auth/`) guards `/swagger` and `/openapi` against
`Swagger:Auth:Username` / `Swagger:Auth:Password`, compared in constant time. It must be registered
**before** `UseSwagger()`, which answers the request itself. Like `ApiKeyEndpointFilter` and unlike
`ApiKeyMiddleware`, a missing password is tolerated **only in Development** (a local `dotnet run`
must not start prompting; dev credentials sit in `appsettings.Development.json`) and answers 503
elsewhere: a host that publishes the API map and forgot the password serves no documentation rather
than open documentation. The gate is HTTP-only, so `--emit-openapi` is unaffected — it reads
`ISwaggerProvider` in-process. In Sankore.Admin this also closes a real hole: `UseApiKeyAuth()` is
registered *after* the Swagger block and so never covered those routes.

Deployment specifics live in `docs/deployment-dokploy.md`.

### Secrets vault

`ISecretsModule` (Kernel) / `AesSecretsModule` (`Shared.Infrastructure/Secrets/`) — AES-256-GCM
values in `secrets.entries`, keyed `(TenantId, Scope, EntityId, Name)`. Registered by
`services.AddSecretsVault(config, configureDb)`, which **validates `Secrets:EncryptionKey` at
start-up** (present, base64, exactly 32 bytes) and fails the boot otherwise. Before that
validation existed, a missing key surfaced as `ArgumentNullException (Parameter 's')` from
`Convert.FromBase64String` on the first write — a 500 for whoever happened to save a provider.

The vault is shared infrastructure, so no module owns its schema: `Program.cs` migrates it in
the startup scope via `SecretsServiceCollectionExtensions.InitializeAsync`, next to the audit
schema. Without that call the table is never created and every write fails with
`relation "secrets.entries" does not exist`.

Per-tenant email credentials go through `NotificationSecrets.CredentialKey(tenantId,
providerType)` (Administration.PublicApi) → `(tenantId, "notifications", Guid.Empty,
"<provider>-credential")`. One credential per provider, so switching SMTP ⇄ Brevo does not
destroy the other's. M12 writes it from `PUT /notification-settings`; M08 reads it at send time
through `VaultNotificationCredentials`. The value never reaches a column, a `GET`, an event or
an audit row.

### Data Protection key ring

Identity's `AddDefaultTokenProviders()` signs every account-activation and password-reset token
with the ASP.NET Data Protection key ring, so the ring's durability *is* the lifetime of those
links. `AddSankoreDataProtection` (`Shared.Infrastructure/DataProtection/`) persists it to
`dataprotection.keys` and pins `SetApplicationName("SankoreCRM")`. Both halves matter: the
framework default writes the ring to the container filesystem (recreated on every redeploy) and
derives the application name from the content-root path (which an image can change silently).
Either one shifting makes an unexpired, unused activation link fail to unprotect, and the only
symptom is `GET auth/activate/verify` answering 400 *"Activation link has expired or already been
used."* — nothing in the logs mentions the key ring.

Like the audit and secrets schemas, it is migrated from the startup scope
(`SankoreDataProtection.InitializeAsync`) and **before** them, because Data Protection swallows a
failed read of a missing table and quietly falls back to an ephemeral in-memory key — which works
until the next restart. Optional overrides: `DataProtection:ApplicationName` (changing it
invalidates every link in circulation) and `DataProtection:KeyLifetimeDays` (the ring's rotation
period, not a token lifespan — old keys stay usable for decryption).

**Token lifespans** are two settings, not one, bound from the `Identity` section
(`Infrastructure/Identity/IdentityTokenOptions.cs`, validated at start-up — a non-positive
TimeSpan fails the boot):

| Key | Default | Applies to |
|-----|---------|-----------|
| `Identity:ActivationTokenLifespan` | 7 days | account activation only |
| `Identity:PasswordResetTokenLifespan` | 2 hours | password reset, email confirmation, change-email |

They are split because they pull in opposite directions — an activation email may sit unread for
days, a reset link is a live credential. Identity has only one knob
(`DataProtectionTokenProviderOptions.TokenLifespan`, shared by every default provider), so
activation gets its own `ActivationTokenProvider` registered under
`ActivationTokens.ProviderName`. Without the split, every day granted to onboarding would also be
granted to password reset.

Provider name and purpose (`ActivationTokens`) are part of the token's purpose chain, so
`CreateUserHandler` (issue), `ValidateActivationTokenHandler` (check) and
`AccountActivationHandler` (consume) must agree on both — drift reads as an invalid token, not as
a bug. `ResetPasswordAsync` is hardwired to the password-reset provider and so cannot validate an
activation token: `AccountActivationHandler` verifies explicitly, then sets the password with an
internal reset token it generates and consumes itself (the idiom `AdminResetPasswordHandler`
already uses). The security-stamp rotation inside `ResetPasswordAsync` is what keeps the
activation link single-use.

Tokens are Base64, so anything that puts one in a URL must `Uri.EscapeDataString` it — a raw `+`
is read back as a space and the token no longer verifies. That is what `CreateUserHandler`
(`activation_url`) and `ForgotPasswordHandler` (`reset_url`) do; `CreateUserHandlerTests`
guards it.

### KYC module specifics (M02)

`KycDbContext` — schema `kyc`, migrations history in `kyc`. The customer is referenced by an
**opaque `CustomerId`**, never an FK into `customers`. Contract:
`Sankore.Modules.Kyc.PublicApi.IKycModule`, which replaced `StubKycModule` — so M01's retention job
and anonymisation handler now see real statuses. `IsRetentionClearedAsync` still answers `false`
deliberately: the retention rule is not implemented and loosening it would let a nightly job
anonymise customers whose evidence a regulator may still demand.

**Ten internal statuses, six in the contract.** `KycFileStatus` (Collecting → Verifying →
Validating → Simplified/Full → UnderReview → Expired, plus ComplementRequired, Rejected,
Suspended) folds onto `PublicApi.KycStatus` through `KycStatusMapping.ToPublicStatus`. Two
judgement calls live there: `UnderReview` → `Approved` (a review in progress does not un-validate
a customer), and `Suspended` → `Rejected` (fail-closed — a gate must never read a compliance block
as "still processing"). The transition table is declarative in `KycFile`; handlers never compare
statuses themselves.

**Four eyes is owned by M02, not by the workflow engine.** `KycFile.Approve` and `Reject` refuse
`LastSubmittedBy` with `KYC_SELF_APPROVAL_FORBIDDEN` — M01 learned on client merges that the engine
runs the circuit but enforces no self-approval rule. The aggregate only sees the final approval and
the rejections, so `DecideKycApprovalHandler` repeats the check on EVERY rung: an intermediate
approval touches no aggregate method, and without it the submitter could sign level 1 of their own
file.

**The approval ladder** (`KycApprovalCircuit`): `Low` → agent alone; `Standard` → agent then branch
manager; `High` or `DuplicateSuspected` → plus the compliance officer. The low-risk ladder is a
deliberate arbitrage by the product owner, departing from KYC-B-05's original "risque faible ou
standard : agent puis chef d'agence". Independently of the rating, a file that has reached the
tenant's `face-match-max-attempts` gains the branch manager — two failed comparisons are a signal
about the capture, not about the customer. That clause is the ONLY thing that adds a second
signature to a low-risk file, and a test pins it as such.

**One open file per customer**, guaranteed by the filtered unique index
`ux_kyc_files_open_per_customer` and not by the read in `CreateKycFileHandler`. Two triggers reach
that command for the same customer — M01's `ClientCreatedEvent` and M13's
`KycRequestedIntegrationEvent`, both fired for a converted lead — so the unique violation is caught
and reported as success carrying the winner's id.

**Field protection is KEYED, not a second `AddFieldProtection` call.** Inject
`[FromKeyedServices(KycFieldProtection.Key)] IFieldEncryptor` / `IBlindIndexer`. The shared helper
binds one `FieldProtectionOptions` and one singleton for the whole container, so a second section
would silently hand M01's data to M02's key; it now throws, and `KycFieldProtection` is the way
round. Keys: `Kyc:FieldEncryptionKey`, `Kyc:BlindIndexKey`, and `Kyc:Storage:EncryptionKey` for the
document images — three distinct secrets.

**Tenant parameters live in M02** (`kyc_settings`, `IKycSettings`, `KycSettingKeys.Defaults`,
seeded per tenant), not in M12: M12 exposes no generic settings store, and a compliance ceiling
belongs to the module that enforces it. Same shape as M01's `customer_settings`.

**The biometry client is GENERATED** from the service's own OpenAPI document, which lives in
`src/Modules/Kyc/.../Infrastructure/Biometry/` and is regenerated on every build (NSwag, via
`OpenApiReference`). It is declared in the MODULE and not in the bootstrapper: `HttpBiometryClient`
is the consumer, and a module may not reference a host. Before it, the wire records were
hand-written and not one field name matched the document — the service reports
`model_versions.service` where the mappers demanded `service_version`, so every call mapped to null
and answered `BIOMETRY_UNEXPECTED_RESPONSE`: every file stuck in Verifying, retried three times,
against a service answering perfectly.

Two things the generation needs. `biometry-openapi.json` is the **3.0 rewrite** of the service's
3.1 document (`biometry-openapi.source.json`), produced by
`tools/normalize-openapi-nullable.py`: NJsonSchema v11 turns `anyOf: [T, null]` into an empty
marker class, which silently makes `mrz`, `birth_date` and even `FieldValue.value` unreachable from
C#. Re-run the script after every refresh — forget it and the build fails on the mappers, which is
the intended failure. And `/UseHttpRequestMessageCreationMethod` exists so a partial class can put
the per-tenant token and the correlation id on each request: `DefaultRequestHeaders` on the pooled
client is how one tenant calls with another's token.

**`POST kyc-files/{id}/verify` carries a `documentType`** (CNI | Passport | Cedeao | Consulaire,
default CNI). `/v1/ocr` requires it to pick its extraction template, and it cannot be derived — the
type is part of the OCR *answer*. It is carried through `ReplayKycVerificationJob` too, or a retry
re-reads a passport as a CNI.

**`/v1/score` is stateless and takes the service's own answers back verbatim**, so
`KycIdentityDocument.EncryptedOcrPayload` and `KycFaceVerification.EncryptedFacePayload` keep them
(`BiometryPayloadProtector`, module key). Encrypted and not jsonb like their neighbours: the OCR
payload repeats the document number in `fields` and in `mrz`, which is the value `EncryptedNumber`
exists to protect — the same reason `OcrFieldsJson` has it stripped. A field correction patches the
corrected value INTO that payload (confidence 1, source `AGENT`) before re-scoring; correcting only
the projection would have the scorer re-grade the misreading and return the same score. A file
verified before those columns existed is not re-scored at all: the correction stands and the caller
gets `ScoreUnavailableCode`, rather than a score computed from a reconstruction.

**Biometry failures are results, never exceptions.** `BiometryResult<T>` separates `Rejected`
(unusable capture — record it, ask the agent for a better photo) from `Unavailable` (the service
told us nothing — leave the file in `Verifying` and let Hangfire replay). Recording an outage as a
rejection would reject an honest client over our own downtime. `FakeBiometryClient` is the double
for every test; the Flask service is not in this repository.

How to take a file from creation to validated — the two accounts the four-eyes rule requires, the
circuit per vigilance level, the degraded manual path and the cases pinning each of them — is in
`docs/plan-test-validation-kyc.md`.

### Lead e-mail is encrypted — never compare it in a query

`Lead.Email` is plaintext in the domain and AES-256-GCM ciphertext in the column (`email_enc`),
applied by an EF value converter in `LeadConfiguration` so no handler can forget it. Three columns,
not one:

| Column | CLR property | Role |
|--------|--------------|------|
| `email_enc` | `Lead.Email` | the encrypted value; read/written transparently |
| `email_blind_index` | `Lead.EmailBlindIndex` | HMAC-SHA256, indexed — **the only searchable half** |
| `email` | `Lead.LegacyEmailClear` | **transitional**, read-only, for pre-migration leads |

**Never write `l.Email == x` or `l.Email.ToLower() == x` in a LINQ predicate.** The ciphertext is
non-deterministic, so on PostgreSQL the literal is encrypted with a fresh nonce on the way down and
the comparison matches nothing — silently, because it compiles and runs. Every exact-match query
goes through `EmailBlindIndex`, computed by `ILeadEmailIndexer`: `CaptureLeadHandler`,
`ConvertLeadHandler` and `FindDuplicatesHandler` all do, each with a transitional fallback on
`LegacyEmailClear` for rows the backfill has not reached.

**The test suite cannot catch this mistake.** Measured: EF InMemory materialises the entity,
decrypts through the converter, and compares in memory — so `l.Email == x` *finds* the row in tests
and finds nothing in production. `LeadEmailProtectionTests.Comparing_the_encrypted_column_works_here_and_would_NOT_work_on_postgresql`
pins the gap. The rule is held by review, not by a test.

The e-mail and its index have exactly **one setter**, `Lead.SetEmail(email, blindIndex)` — an index
left behind by a changed address makes dedup return the wrong rows. `Lead.Update` has no `email`
parameter for that reason; `ApplyMergeOverrides` takes both halves.

`LeadEmailProtectionBackfill` runs in `LeadsModule.InitializeAsync`, **before the host serves its
first request**, in batches of 500, idempotent and resumable. It logs a warning with the number of
rows left: **do not drop `leads.leads.email` until that is zero.**

**`Leads:BlindIndexKey` is shared** by `HmacPhoneBlindIndexer` (phone) and the keyed
`HmacBlindIndexer` (e-mail and everything else). No collision — the shared one prefixes its input
with the purpose — but rotating that key invalidates BOTH indexes and both need a backfill.

### Existing-customer leads (BNF-B-22)

`Lead.CustomerId` is an opaque M01 reference, **server-set only** and absent from
`CaptureLeadRequest` / `SystemCaptureLeadRequest`: an attachment decides which client record the
whole request reads its identity, household, PEP and FATCA from, so a public ingest able to set it
would point a lead at an arbitrary client. `Lead.Kind` is **derived from it, never stored**.

`Lead.ProductLineId` is the same shape — opaque, server-set, no FK — and does **not** replace
`InterestedProduct`, which stays as captured. That field already links a lead to a product but by
**free-text label** (`"Crédit commerçant"`, defaulted to `"Unknown"` by the pull job and the system
capture endpoint), and a label cannot carry a beneficiary policy. The pair is the same one as
`Lead.Source` / `Lead.LeadSourceConfigId`: what the prospect said, and what an agent resolved it to.

**`CustomerMatchPolicy` is the single definition of when a lead attaches automatically**: a UNIQUE
match, on an identifier that identifies a PERSON (identity document, e-mail). A **phone alone never
attaches** — a family handset or a shop line is ordinary here — and several candidates never
attach. Both the capture handler and the suggestion endpoint read it, so they cannot drift.

`ICustomersModule.FindMatchesAsync` takes **clear values**: M13 and M01 hold different blind-index
keys, so no caller can compute an index M01 would match. Same reason `CreateFromLeadRequest` carries
clear text.

**Cross-module reads inside a command must suppress the ambient transaction.** `TransactionBehavior`
holds a `System.Transactions` scope and the other module reads through its own DbContext; a second
connection enlisting promotes it to a distributed transaction, which Npgsql does not support and
which fails at run time. `CaptureLeadHandler.TryMatchCustomerAsync`, `ClientAdvisorResolver` and
`ConvertLeadHandler` all wrap the call in `TransactionScopeOption.Suppress`. Those lookups also
**swallow failures**: losing a lead because M01 is down is far worse than not recognising a customer.

### Shared person value objects (BNF-B-02)

`PersonIdentity` and `PepStatus` (`Sankore.Shared.Kernel/ValueObject/`) are the one shape for a
natural person — the lead's principal, a spouse, a child, a beneficiary. Each module maps them into
its own schema through **`PersonMapping.OwnsPersonIdentity` / `OwnsPepStatus`**
(`Shared.Infrastructure/Persistence/`), passing its OWN keyed `IFieldEncryptor`: the column list is
shared so a conversion snapshot lines up field for field, the keys are not.

`Lead` and `Client` keep their flat columns and are NOT retrofitted — those columns hold encrypted
PII already in production, and converting them to `OwnsOne` would run through `SearchKeyBuilder`,
`ClientMatchScorer`, the importers and the audited reveal endpoint for a gain of pure form. The
value objects are for the NEW entities.

Two things to know before mapping them:

- **`LastName` and `FirstName` are required; everything else is optional.** An owned type whose
  every column is null is one EF cannot tell from an absent owner. A caller holding a
  partially-known person carries `PersonIdentity?` and leaves it null.
- **`PepStatus.IsExposedAt(asOf, postMandateMonths)` is the single definition of the post-mandate
  window.** Never compare `FunctionEndDate` by hand: M13's completeness, M13's dispatching and
  M01's recalculation would each grow their own version and disagree about the same person. The
  months come from `IAdministrationModule.GetComplianceSettingsAsync`.

**Validators live in `Shared.Infrastructure/Validation/`, not next to the value objects** — the
Kernel has zero third-party dependencies by design, FluentValidation included. Invariants that
condition the type's existence stay in the factories and throw; request-shaped rules worth a 400 go
in the validators. Compose them with
`RuleFor(x => x.Identity).SetValidator(new PersonIdentityValidator())`.

`EncryptedConverters.Date` serialises as `yyyy-MM-dd` with `InvariantCulture` **on both sides**, and
that is load-bearing: encrypted, a round-trip through the process culture is silent — the value
decrypts perfectly and is simply the wrong day, the same trap the spreadsheet readers already paid
for. A converted column is non-deterministic ciphertext, so it can never be indexed or compared in
SQL; that is what the blind index is for, and it stays a separate column.

### A lead's household is a child of the aggregate (BNF-B-03)

`LeadHouseholdMember` is reached **only through `Lead`**, because every invariant is about the SET:
at most one active spouse, the child ceiling, the age limit. **`Include(l => l.HouseholdMembers)`
plus `AsTracking()` is mandatory, not an optimisation** — a lead loaded without them sees an EMPTY
household and happily adds a second spouse, caught only by `uq_lead_one_spouse` as a raw SQL error.
`HouseholdWriteContext.LoadAsync` is the one place that gets this right; go through it.

**There is no `LeadStatus.Assigned`** and never was — assignment is `CurrentAssignmentId`. So
`Lead.IsHouseholdEditable` says what the rule means: not terminal AND somebody is working it, where
`OwnerId` counts as much as an assignment (a counter capture never routed through the engine still
has somebody responsible).

**Address inheritance is enforced on WRITE**, not just honoured on read: `UsesPrincipalAddress`
clears the member's postal fields via `PersonIdentity.WithoutPostalAddress()`. A stale address left
in the row is one a later "stop inheriting" resurrects, years out of date. Read through
`member.ResolveIdentity(principal)` — a projection reading `Identity` directly shows a child with no
address, which looks like missing data rather than inheritance.

Removal is **logical** (`RemovedAt` + a required reason), never a delete.

### PEP exposure (BNF-B-05)

`Lead.HasPepExposure` is a **column**, written only by `RecalculateHasPepExposure`: dispatching
filters on it in SQL, and the event has to fire at the exact moment the value changes, which a
computed property cannot notice. The method is public so a nightly job can call it — **exposure can
lapse with nobody touching the lead**, when the post-mandate window closes.

Propagation: a principal exposed as `Self` sets `FamilyMember` on the active household, **except**
on a member carrying an `OverrideReason` — that one was classified by an agent, and overwriting it
discards a judgement the story grants. A principal exposed as `FamilyMember` propagates nothing.

The PEP **detail** (function, country, link) sits behind `lead:pep:read`, and the gate is in the
**endpoint**, where an authorization reviewer looks. The **flag** stays visible to anyone who can
see the lead.

**`PepStatus.NotExposed` returns a FRESH instance on every access, and that is load-bearing.** The
type is mapped as an EF owned entity, so a shared static makes every unexposed person the same
tracked object and the second row fails with *"'Lead.PrincipalPep#PepStatus.LeadId' is part of a key
and so cannot be modified"*. Any static "empty" value of an owned type needs the same treatment.

**`db.Entity.Update(entity)` is now actively harmful on an aggregate with owned types** — it marks
the owned entity's identifying key as modified and EF refuses the save. Seven Leads handlers had a
redundant `db.Leads.Update(lead)` after an `AsTracking()` load; all were removed. Never call
`Update` on something already tracked.

### Per-person questionnaires and health data (BNF-B-07/08/09)

A `QualificationResponse` is identified by **`(lead, template, subject type, subject id)`**, held by
`ux_qualification_responses_subject`. A lead-scoped questionnaire stores the **lead's own id** as
the subject, never null — PostgreSQL treats each null as distinct, so the constraint would constrain
nothing.

`ExpectedResponsePlanner` creates the `ToComplete` rows **in the same transaction** as the household
change that caused them, and is idempotent by diff. Removing a member deletes nothing: the planner
reads **active** members only, so the expectation goes and the row stays.

**`QuestionnaireVersion` is frozen at creation.** A response re-read through the live template would
be re-interpreted against questions the person was never asked — for a health declaration that is
the difference between a valid answer and a misrepresentation.

`ExcludeFromScoring` is **forced** true for a health questionnaire, not defaulted — a tickbox an
administrator could clear would make the prohibition a configuration. A health questionnaire is also
only attachable to an insurance category, and the Kernel has **three** of those (`Insurance`,
`HealthInsurance`, `ForecastInsurance`); the rule is re-checked in `UpdateDetails`, which does not
go through `SetSubjectScope`.

**Health answers live in a second column, not behind a conditional converter.** `answers` stays
plain jsonb; `health_answers_enc` is encrypted in bulk. A value converter sees only the value, never
the row, so one column cannot be "jsonb unless another field says otherwise" — and two columns make
the dangerous direction impossible. **No blind index**, deliberately: nothing searches for a person
by their health answer, and an index would make "who answered yes to question 7" a query.

Health consent is **per person and must be active at the time of the write** — a consent for the
principal does not cover a child, and a withdrawn one stops collection. It is
`ConsentType.HealthData` plus `SubjectType`/`SubjectId` on `LeadConsent`, not an entity of its own.

**`HealthAnswerAccess.Read` is the only door** to answers in clear, and `DecryptedHealthAnswers`
exists so an architecture rule can NAME it. `ScoringIsolationTests` asserts nothing outside
`Features/Questionnaires` depends on either — plus a case asserting the dependency DOES exist
inside the slice, because "nothing outside X depends on Y" passes trivially once Y is renamed away.

The retention purge counts from **transmission**, not creation: until the answers reach the insurer
they are the only copy. A transmission that never happens therefore leaves data behind — the right
failure, since the alternative destroys a declaration nobody received.

### FATCA (BNF-B-11)

`FatcaDeclaration.Status` is **computed, never set** — `ComputeStatus` is the whole rule and its
evaluation ORDER is the logic: a refusal wins; a W-9 means `UsPerson` even with no index declared
(an index is an indicator, a W-9 is the person stating the fact); no index at all means
`NonUsPerson` even if a W-8BEN was produced (`IndiciaCured` would assert indices were lifted that
never existed); W-8BEN against declared indices cures them; anything else is pending.

**`NotApplicable` is never stored.** It belongs to the TENANT, so storing it would mean rewriting
every declaration when the toggle moves. `EffectiveStatus(fatcaEnabled)` is the read path, and
declarations collected while FATCA was on are retained rather than reinterpreted.

The seven indices stay **individually identifiable** (not one `hasIndicia` flag), because the
evidence that cures an index depends on which index it is.

A change of circumstances **supersedes**, never edits — and the old row is superseded *before* the
new one is added, so `ux_fatca_declarations_active_per_subject` never sees two active declarations.
The ordering matters, not just the pair.

**FATCA off refuses the write** rather than storing and ignoring it: a declaration held by an
institution with no obligation is a US TIN kept for no lawful reason. A compliance-settings
**outage refuses too** — guessing on would collect a TIN with no obligation to hold it, guessing off
would skip a legally required declaration.

`FatcaStatusChanged` carries the status and the subject, **never the TIN or a document reference**,
and does not fire when the status is unchanged.

### An audit actor is never caller-supplied

`LeadConsent.RecordedBy` names the **authenticated user** who recorded the entry, and
`LeadConsent.GivenBy` names the **person** who consented — a parent for a minor child. The two are
separate columns and must never be collapsed: writing the giver into the recorder lets an agent
erase themselves from, or attribute to a colleague, the one record a regulator asks about first.
BNF-B-09 lists them separately for that reason.

The same shape applies to any `RecordedBy`/`CreatedBy`/`UpdatedBy` in this solution: take it from
`ICurrentUser`, never from the command. A caller-supplied actor id is also unvalidated by
definition — `HealthConsentGuard.EnsureRespondentValid` is what constrains a giver to the principal
or the spouse, and it is shared with BNF-B-08's questionnaire respondent so "who may act for a
child" has one definition.

### EF: a collection child with an app-assigned Guid key needs ValueGeneratedNever

EF treats a `Guid` key that is already set as store-generated, so an entity appended to a **loaded**
collection navigation is tracked `Modified` rather than `Added` — and `SaveChanges` attempts an
UPDATE on a row that was never inserted (*"Attempted to update or delete an entity that does not
exist in the store"*). `LeadHouseholdMemberConfiguration` sets `ValueGeneratedNever()` on the key for
exactly this reason.

**Tests will not catch it if they add children to a new parent**: the whole graph is `Added` and the
child inherits the state. Only load → modify → save exposes it, which is what every endpoint does.
Adding through `db.Set<T>().AddRange(...)` is immune (it forces `Added`), which is why the
qualification-template update path never hit this.

### Household and PEP reads are ownership-scoped, unlike other lead reads

Every other lead read in M13 is scoped by the tenant query filter plus a permission and nothing
else — no Leads request implements `IAgencyScopedRequest`. `GetHouseholdHandler` is the exception
and calls `LeadAccessGuard.EnsureCanWorkAsync` before mapping, because what it returns is a
spouse's and children's identities plus a PEP classification: leaving it open while the writes
enforce ownership would make the write guard decorative.

Using the write guard on a read is safe **only because of its ordering** — bypass roles and direct
ownership are checked before the M12 supervision lookup, so a lead's own holder never reaches the
call that fails closed. Any new read over household, PEP or FATCA data should do the same; a read
over ordinary lead fields should not, or it diverges from the whole module.

### Cross-module failure asymmetry

The three postures are deliberate and should be matched, not unified:

| Call site | On an outage | Why |
|---|---|---|
| customer / product-line resolution **at capture** | best-effort, swallowed | a prospect on the phone must never be lost |
| `LeadAccessGuard` | **fails closed** | it decides who may edit another agent's lead |
| `HouseholdPolicyResolver`, PEP window | **fails the write** | letting it through bypasses a child ceiling and an LBC/FT window an institution configured; the agent retries |

An **unresolved** product line is allowed (an agent collects then qualifies); a **dangling** line id
is allowed too, because the reference is opaque by construction and refusing would strand a lead
whose product somebody else removed.

### Product lines carry a beneficiary policy (BNF-B-06)

`ProductSpeciality` **is** the product line — there is no separate `ProductLine` entity, and
`Lead.ProductLineId` is its `Id`. Two members were added:

| Stored as | What | Why |
|---|---|---|
| `beneficiary_policy`, typed jsonb | `HouseholdEnabled`, `AllowedRoles`, `MaxChildren`, `ChildMaxAge`, `RequiredFieldsBeforeConversion`, `RequiredKycTier`, `IntegrationSystem` | eight optional fields M13 and M01 read **whole** and never filter on |
| `allow_multiple_holdings`, a column | `AllowMultipleHoldings` | it **is** queried (BNF-B-22's duplicate-request warning), and it is not a designation rule |

`AllowMultipleHoldings` defaults to **true**: a savings account, a tontine or a credit are
legitimately held several times, and it is the exception — one health policy per person — that an
administrator declares. `BeneficiaryPolicy.None` is the opposite default: an unconfigured line
enables no household and accepts no designation, because a line nobody has set up must not quietly
start collecting a spouse's identity.

**EF generates `DEFAULT ''` for a non-nullable converted property, and PostgreSQL rejects `''` on a
jsonb column** (`invalid input syntax for type json`) — the migration fails rather than the data
being wrong. The mapping sets `HasDefaultValueSql("'{}'::jsonb")`; `{}` deserialises to
`BeneficiaryPolicy.None` because every member of the record is optional. Any future typed-jsonb
column needs the same treatment.

A converted property holding **lists** also needs an explicit `ValueComparer`
(`BeneficiaryPolicyComparer`). EF's default compares record-wise, and record equality does not
compare list elements — so adding a role would leave the entity looking unchanged and the write
would be silently dropped.

### Resolving a product label to a product line

`IAdministrationModule.ResolveProductLineAsync(tenantId, label)` bridges `Lead.InterestedProduct`
(free text) to a catalogue id. **Exact** on the code or the name, casing and accents neutralised,
active products only — never fuzzy, because the resolved line decides which fields gate a
conversion and which KYC tier is required.

- **Code before name.** A code is an identifier an administrator chose; a name is a label two lines
  can share — and when they do, resolution returns **null** rather than row order.
- **`"Unknown"` resolves to nothing.** It is the literal default `PullLeadSourceJob` and
  `SystemCaptureLeadEndpoint` write when a source names no product: the absence of a label, not a
  label. A tenant with a product actually named "Unknown" would otherwise collect every unlabelled
  lead in the deployment onto its policy.
- Matched **in memory** over the cached catalogue, not in SQL: accent-insensitivity would need
  `unaccent` plus functional indexes on two columns, and a tenant's catalogue is tens of rows.

`ResolveQualificationTemplate` takes an optional `productLineId` and **prefers it over the label** —
one cached read yields the authoritative code and category, which also skips level 2's
`GetProductCategoryAsync`. Without it, a label spelling a code the catalogue lacks makes levels 1
and 2 both miss and every lead quietly gets the generic template.

### ProductCatalogCache

The 5-minute cache of BNF-B-06, over `IDistributedCache` (Redis is provisioned; **resolve it with
`GetService`**, since it is absent in unit tests). Two keys: the policy **per line**, read per lead,
and the active catalogue **per tenant**, read per label resolution.

**It is a service, not code inside the facade**: the facade reads and five write handlers
invalidate. A cache whose reader and writer disagree about a key never invalidates, and the only
symptom is a five-minute delay that looks like flakiness. **Invalidate AFTER `SaveChanges`** —
doing it first opens a window for a concurrent read to repopulate from the pre-write row, which is
the one ordering that makes a cache permanently wrong instead of briefly stale.

### Tenant parameters: three module-owned stores, one typed table

M12 exposes **no generic settings store** — its only precedent for a tenant parameter is
`TenantNotificationSettings`, a dedicated table. So each module owns its own:

| Store | Shape | Owner | Read by |
|-------|-------|-------|---------|
| `customers.customer_settings` | key/value + `ICustomerSettings` | M01 | M01 |
| `kyc.kyc_settings` | key/value + `IKycSettings` | M02 | M02 |
| `leads.lead_settings` | key/value + `ILeadSettings` | M13 | M13 |
| `administration.compliance_settings` | **typed, one row per tenant** | M12 | M13, M01, M02 |

`compliance_settings` is typed and not key/value because its values have real invariants (a GIIN has
a fixed 19-character structure, and enabling FATCA without one is a half-configured regulatory
obligation) — a `Value string` column pushes both rules into a parser every caller must remember to
run. It sits in M12 and not M13 because three modules read it: a `leads_settings` row would make M01
and M02 depend on M13 to learn whether their own institution is subject to FATCA.
`IAdministrationModule.GetComplianceSettingsAsync` **never returns null** — a tenant with no row
runs on the platform defaults, and returning null would hand every caller the same question to
answer. `AdministrationModuleFacade.PlatformDefaultCompliance` duplicates
`ComplianceSettings.CreateDefault`, and `ComplianceSettingsDefaultsTests` pins the two together.

**FATCA is a tenant switch; PEP vigilance is not.** FATCA depends on the country's
intergovernmental agreement or on the institution's own IRS registration. PEP vigilance follows the
LBC/FT rules binding every UEMOA institution — `PepPostMandateMonths` tunes the window, nothing
turns it off.

**The key/value services cache in a `static` dictionary** (60 s TTL, process-local, by design).
In tests that means a fresh tenant id **per test** — two tests sharing one see each other's writes
whatever order they run in. xUnit builds one instance per test, so an instance field is enough.

### Architecture tests

`NetArchTest.Rules`, referenced by `Leads.Tests`. `Architecture/ScoringIsolationTests.cs` pins
BNF-B-09: `LeadScoreCalculator` and the whole `Features.DispatchLead` namespace must not depend on
`QualificationResponse`.

**NetArchTest reports success for an empty selection**, so a renamed class or a moved namespace
turns such a rule green while checking nothing. Every rule needs a companion case asserting its
selector still matches something — `ScoringIsolationTests` has one.

### Lead completeness is ONE calculator (BNF-B-04)

`LeadCompletenessCalculator` (`Features/Enrichment/`) is the single definition of "what is
missing". The percentage on the lead screen, the per-person gap list, the enrichment response and
the **conversion gate inside `ConvertLeadHandler`** are the same execution. Never compute a second
one: a screen with its own arithmetic eventually shows 100% on a lead the conversion refuses.

**An outage makes a lead INCOMPLETE, never complete.** Every cross-module read falls into a
`COMPLETENESS_CHECK_UNAVAILABLE` gap that blocks. Answering "complete" because M12 was unreachable
is the one failure mode a gate must not have, which is why the class never catches without adding a
gap.

`CompletenessGap.Blocking` separates a refusal from a report. Almost everything blocks — a list
called "required fields BEFORE conversion" is a gate by definition. The exception is
`FATCA_DOCUMENTATION_OUTSTANDING`: the indices are declared and the institution is waiting on the
client's form, which is a compliance-queue item, not a reason to stop a subscription. It still
counts against the percentage.

`ActionHint` says where a gap is fixed when the lead is not the place: `FIX_ON_PRINCIPAL` for an
address on a member that inherits one (its own update command refuses postal fields), and
`FIX_ON_CUSTOMER` for anything on an attached lead's client record.

**`PRODUCT_LINE_NOT_RESOLVED` is reported always and blocks only behind a tenant switch**
(`LeadSettingKeys.RequireProductLineToConvert`, default **false**). A deliberate departure from
BNF-B-04 §3.3's default: `Lead.ProductLineId` is new, label resolution is exact-match only, and the
pull job writes the literal `"Unknown"`, so shipping the refusal on would make every lead already
in the database unconvertible on deploy. Turning it on is an operational step that follows
catalogue configuration.

### Required-field codes are a closed list in the Kernel

`PersonFieldCode` (`Shared.Kernel/ValueObject/`) — 14 codes plus the scope of people each may be
asked of. **M12 refuses an unknown code at write time**
(`BENEFICIARY_POLICY_UNKNOWN_REQUIRED_FIELD:<code>`) and M13 evaluates them. Before it, the list was
free text, so a typo in a product's configuration was a requirement that silently never applied.

- `PersonFieldScope.PrincipalOnly` (`PHONE`, `EMAIL`, `NATIONAL_ID`, `DESIRED_AMOUNT`) is not asked
  of the household: there is no column for a spouse's phone, so the gap would be unclosable.
- `RequiredFieldEvaluator`'s switch **refuses by default** (`_ => false`). A requirement nothing
  evaluates must block, not pass. Adding a constant without a case lands exactly there.
- Adding a code is a change in two places. That is the honest cost, and far cheaper than a
  requirement that reads as configured and enforces nothing.

### "Not exposed" and "never asked" are different answers

`Lead.PepDeclaredAt` / `LeadHouseholdMember.PepDeclaredAt` carry the difference, because
`PepStatus.NotExposed` is an owned type's **default** and the status alone cannot. Null means
unasked — true of every pre-Lot-6 row, which is why that migration has no backfill: stamping one
would manufacture a declaration. `PEP_NOT_DECLARED` reads it; an explicit negative satisfies it.

A member's marker is **passed explicitly** by `Lead.AddHouseholdMember`, never inferred from
`pep is null`: the caller always supplies a status (the propagated default is one), and only the
caller knows whether anybody answered. An unexposed principal propagates "not exposed", which is
not an answer about the spouse.

### The lead's principal is a whole person (BNF-B-02)

Five columns on `leads` complete it: `principal_usage_name`, `principal_address_enc` (**encrypted**,
uncapped, never comparable in SQL), `principal_postal_code`, `principal_city`,
`principal_country_code`. Without them `PersonIdentity.WithAddressOf` had nothing to copy, so every
member inheriting the address resolved to a null one.

**One documented asymmetry**: the principal's birth date stays CLEAR (`date_of_birth`, a column the
importers, the duplicate scorer and several projections already read) while a household member's is
encrypted. Encrypting it is a migration across all of that, deliberately out of scope — it is named
on `Lead.PrincipalIdentity` rather than left to be discovered.

`Lead.EnrichPrincipalIdentity` is the writer: **null leaves alone, blank clears**. Enrichment spans
several conversations from a screen that posts only what it collected, so a null that cleared would
make each save erase the last. It deliberately excludes the PEP status (its own command — a `Self`
exposure reclassifies the whole household and the audit row must say so), the e-mail (`SetEmail`
maintains the blind index), the household, the questionnaires and FATCA.

### `HouseholdWriteContext` has two loads, not one with a flag

`LoadLeadAsync` = load with members + staleness + ownership + PEP window. `LoadAsync` = that plus
the product line's household rules. Enrichment uses the **first**, because the policy gate answers
`HOUSEHOLD_DISABLED_FOR_PRODUCT_LINE` on a line that records no household — right for adding a
spouse, absurd for writing a current-account principal's identity. Two methods rather than a
boolean: a gate flag is one a call site eventually passes the wrong way round.

### Conversion branches on `Lead.Kind` (BNF-B-12, BNF-B-24)

| | New prospect | Existing customer |
|---|---|---|
| M01 creation | `CreateFromLeadAsync` | **none** — `lead.CustomerId` |
| Lead-duplicate gate | applied | **skipped** |
| `ConversionType` | `NewCustomer` | `ExistingCustomerNewProduct` |

The duplicate gate is skipped because it looks for other **leads** matching the same person, and for
a customer asking for a second product those are the expected state — the client record lists them
as requests in progress. The duplicate question that does apply is `AllowMultipleHoldings` against
the holdings.

**`Lead.Convert(customerId)` is idempotent.** It used to accept a second call, re-stamp
`ConvertedAt` and re-publish `LeadConverted`, so a back office was told twice to open the same
holding. Converting to the same customer now succeeds and changes nothing (`AlreadyConverted: true`,
nothing published); to a different one is refused. Naming a client other than the one the lead is
attached to is `CUSTOMER_LINK_CONFLICT`, not a choice to honour silently.

`LeadConverted` carries `ConversionType` and `ProductLineId` (neither is derivable — both paths end
with a lead pointing at a client). `KycRequested` carries `HasPepExposure`, **the flag only**: the
function, country and family link stay behind `lead:pep:read`.

In M02 the vigilance level is **raised, never lowered**, and it is applied on the
already-existed path too — which is the *normal* case here, since `ClientCreatedKycConsumer`
usually wins the race. Applying it only on creation would leave the PEP flag with no effect in the
common case.

### An attached lead reads its identity from M01, and that is only half of BNF-B-23

Delivered: an attached lead's principal fields are **never required again** (the client record is
the source), and the client file is checked instead — existence, a sellable status, KYC approved and
unexpired, the tier the product line requires. Each blocking gap points at the client record.

**Not delivered, deferred to the client-household lot**: refusing household / PEP / FATCA *writes*
in the `leads` schema for an attached lead. The rule is right, but M01 exposes no household yet, so
refusing now would leave an agent nowhere to record one. Completeness therefore still reads the
members the lead carries — transitional, not the target.

`QualificationSubjectType.CustomerPrincipal` / `CustomerHouseholdMember` stay **unproduced**.
Switching the subject type on `Lead.Kind` looks right and is not: a lead whose principal already
answered as `Principal` and was attached afterwards would instantly owe a second
`CustomerPrincipal` response, its answered row ignored and its conversion blocked. The subject of a
questionnaire asked ON a lead is that lead's principal, whoever the lead belongs to.

### The client's household IS `client_relationships` (BNF-B-13)

There is no `CustomerHouseholdMember` table, deliberately: that one already modelled a spouse and a
child, internally (`RelatedClientId`) and externally, with temporal validity and reciprocity — and
the logical removal the story asks for was already `ValidTo` + `CloseReason`. A parallel table would
give two places to record a spouse, which is the divergence the whole evolution exists to remove.

The household is the filtered view `ClientRelationship.IsHouseholdMember`:
`Type ∈ {Spouse, Child, Dependent}` and `ValidTo IS NULL`. A guarantor or a proxy is a relationship
and goes through the relationships endpoints.

**A non-client member's identity is flat columns plus a projection** (`ResolveIdentity`), never an
owned `PersonIdentity` — the same call as `Lead.PrincipalIdentity`, because `ExternalFullName`,
`ExternalDateOfBirth` and `EncryptedExternalDocumentNumber` are already read by the reveal endpoint,
the merge executor and the importers. **`ExternalFullName` is kept in step** by `ApplyIdentity`:
leave it stale and a renamed spouse shows their old name everywhere except the household screen.

`SpouseUnionType` lives in the **Kernel**: the conversion copies it from M13 to M01, and two
per-module enums would be two lists that drift.

**The client household has NO child ceiling.** `ClientHouseholdDto.MaxChildren` is always null — a
ceiling is a product-LINE rule and a client holds several lines at once. The lead side has one
because one lead is one request for one line. The field exists so the two screens share a shape.

**Two indexes over the same properties need the named `HasIndex(expr, name)` overload _plus_
`HasDatabaseName`.** EF identifies an index by its property set, so a second one over
`(tenant_id, client_id)` silently REPLACES the plain index every relationship read uses — and
without the database name the column comes out as `..._client_id1`.

### `Client.HasPepExposure` is written in one place and only a RISE is an event

`HouseholdWriteContext.CommitAsync` recalculates it on **every** household write — a spouse added, a
child removed, a status declared — because any of them can move it. A handler that saved on its own
would leave a client reading "not exposed" with an exposed spouse on file.

`Client.RecalculateHasPepExposure` returns true only when the flag ROSE, and
`ClientPepExposureRaisedEvent` fires only then: publishing on a fall would ask a compliance officer
to re-review a file because a mandate expired. The event carries **no classification** — the
consumer opens a review, it does not read why.

A client exposed as `Self` propagates `FamilyMember` onto the household **except** onto a member
carrying an `OverrideReason`. Note what that means in practice: `PepStatus.NotExposed` drops the
reason, so the guard protects a **declared exposure** only — a test pins exactly that.

### `ProductHolding` is the CRM's capture, not a copy of Integration's read models (BNF-B-14)

`InsuranceSubscription` (the saga) and `PolicyRecord` (what the insurer holds) stay in Integration;
the link is `ProductHolding.ExternalRef`, filled when the back office answers.

| Rule | Where it lives | Why there |
|---|---|---|
| Which transitions exist | `ProductHolding.Allowed`, a table | a status comparison spread across slices is a machine nobody can read (M02's lesson) |
| The signature gate | re-checked in `SubmitForSync` | `Sign` does not move the status, so `PendingSignature` can be signed or not — B-16 gates on the signature, not the status |
| One live holding per line | handler **and** `ux_product_holdings_live_per_line` | `AllowMultipleHoldings` is in M12's catalogue and a partial index sees only its own table; without the handler the index would forbid a legitimate second savings account |
| Read-only when imported | the aggregate | the authoritative copy is elsewhere, so a CRM edit is silently overwritten by the next reconciliation |

`ConfirmSync` and `RecordRejection` have **no endpoint**: that is the back office answering, which
arrives as an integration event. `ProductHolding.Imported` is a separate factory, not a walk through
the state machine — an imported holding was never a draft here and never signed here, and walking it
would invent a signature date nothing witnessed. **Returning a rejected holding to draft clears the
signature**: the corrected terms are not what the client signed.

`HoldingSpecifics` is a **flat record, not a polymorphic hierarchy** — which is how the Swashbuckle
discriminator trap is avoided by construction rather than by remembering a schema filter. Nothing
dispatches on it and every consumer reads it whole.

### A beneficiary clause is an atomic SET (BNF-B-15)

`ProductHolding.Designate` replaces the whole clause. An add-one endpoint would either refuse every
intermediate state — making a three-beneficiary clause impossible to enter — or accept an invalid
one, because the invariants are about the set: a role's cardinality, and the shares of a rank.

- **Shares total 100% PER RANK, never across**: rank 2 inherits only if rank 1 cannot, so each rank
  allocates the whole capital. Exact `decimal`, no tolerance — 33.33 + 33.33 + 33.34 is what an
  agent types, and rounding would hide a clause that does not add up until the claim.
- **On an `Active` holding a change writes a new VERSION** (an avenant) and closes the previous one
  with `ValidTo`; in draft it replaces. Versioning every keystroke would bury the real avenants.
- **`PendingSync` refuses a clause change**: the request is already in a queue and would be sent as
  neither version.
- The subject is an **opaque pair** (`SubjectType`/`SubjectId`) — a client, a `ClientRelationship`,
  or nobody at all (a death-benefit clause may name a friend). The same person is designable on
  several holdings, so **nothing is unique per subject**; the duplicate rule is per submission.
- A departing household member's designations move to **`ToReview`**, never deleted and never
  reallocated: the rank no longer totals 100, and that is precisely what the agent must be shown.
  `HouseholdMemberRemovedEvent` fires only when something was actually flagged.

### Two modules may declare the same DTO name — the schema ids handle it

Swashbuckle's default schema id is the short type name, so the first time two modules declare a
`PersonIdentityDto` the **whole document generation throws** (`Can't use schemaId …`). That is a
start-up failure invisible to build and tests, and `--emit-openapi` is what catches it.

`SwaggerSchemaIds` (`Sankore.Api/Infrastructure/`) answers: the short name, except for short names
at least two modules declare, which get their module prefixed
(`CustomersPersonIdentityDto`). Qualifying everything would rename every model in the Angular client
at once — hundreds of breaking renames to fix one collision.

### A holding reaches its back office as a queued command (BNF-B-16)

The chain is `M01 ProductSubscriptionRequested` → `HoldingSubscriptionConsumer` (Integration) → one
`IntegrationCommand` → the INT-06 dispatcher → `IntegrationCommandSucceeded`/`Rejected` →
`HoldingSyncConfirmed`/`RejectedConsumer` (M01) → `Active` / `Rejected`. **Register all of them or
none**: a holding whose consumer is missing sits in `PendingSync` for ever with nothing to move it,
which is exactly what Lot 7 shipped before Lot 8 closed it.

Nothing about the retry policy, the error families, the rejection queue or the dispatcher was
rewritten. What changed in the existing code is small and deliberate:

- `InsurancePolicyPayload` gained `CrmHoldingId` + `ClauseVersion` (optional, back-compatible).
- `IdempotencyKeyFactory.ForHoldingSubscription` keys by HOLDING and CLAUSE VERSION. The old
  `ForPolicySubscription` keys by customer + product + date and still serves callers with no
  CRM-side contract record — the comment claiming no module owns one was true before
  `ProductHolding` existed and is not any more. **Without the version in the key an avenant is
  swallowed as a replay** and the insurer never learns about it.
- `IntegrationCommandSucceededEvent` is the generic success signal, published in
  `PublishSuccessAsync`'s `default:` branch. `CreateCustomer` and `OpenAccount` keep their richer
  events; everything else had no CRM-side signal at all.

**`HoldingSyncService` is NOT behind MediatR**, unlike every other write in its zone. Its callers
are consumers in another module under SYSTEM identity: `HoldingWriteContext` would check the
consumer's agency perimeter, and `AuditBehavior` would record SYSTEM as the author of a change the
insurer made. It is `public` for the same mechanical reason `SubjectLabelResolver` is — MassTransit
instantiates the consumers, so they are public, and a public type cannot take an internal
constructor dependency.

**Idempotence is business-level, not just inbox-level.** Confirming an already-`Active` holding with
the SAME reference succeeds and changes nothing; a DIFFERENT one is refused
(`HOLDING_EXTERNAL_REF_CONFLICT`) — two external references for one contract means one system is
wrong, and picking a side silently is what a reconciliation cannot undo. The inbox cannot catch
this: two distinct messages can carry the same outcome. A success with **no** reference leaves the
holding in `PendingSync`, because `ExternalRef` is the only link between the two systems and an
`Active` holding without one is irreconcilable.

The signature is **re-checked in the consumer** even though the transition to `PendingSync` already
required it. This is the last code that runs before a contract leaves the institution, and it must
not take a second-hand word for the client's consent.

### Lead anonymisation is five tables in one transaction (BNF-B-19)

`AnonymizeClosedLeadsJob` — per tenant, SYSTEM identity, 03:45 (after the 03:30 health purge, which
reasons about transmitted answers on leads of any age). It covers `Lead`, `LeadHouseholdMember`,
`QualificationResponse`, `LeadConsent` and `FatcaDeclaration`, **all five or none**: a lead whose
name is gone and whose spouse's is not is half-anonymised, and nothing would ever come back to
finish because it no longer matches the selection.

- **The blind indexes go.** Leaving one lets somebody confirm a guessed phone or e-mail against a
  row whose data is supposedly destroyed — a blind index working as designed.
- **The PEP classification goes.** Personal data *and* a compliance record; the file it justified
  closed two years ago, and keeping a political exposure on a destroyed lead holds the most
  sensitive field of all for no remaining purpose.
- **The funnel stays** — source, campaign, score, status, stage, agency, dates, and the id so an
  audit row still resolves. Destroying it would make a retention rule a reporting outage.
- **An `ExistingCustomer` lead loses only its OWN data.** The job holds no reference to M01 at all,
  so the isolation is structural rather than a condition somebody could forget. The `CustomerId`
  link survives: it is funnel history.

`lead-retention-months` defaults to 24. **Zero or negative SKIPS the run** rather than being
honoured — that is what a settings typo produces, and the mistake is irreversible.

### A designated subject is validated against the household, not just shaped

`ProductHolding.Designate` checks a subject's SHAPE — an external person is named and carries no
id, anybody else carries one — and that is as far as an aggregate can go. **`DesignationSubjectValidator`
does the rest**, on both paths that accept a clause (`DesignateHandler` and
`SubscribeToProductHandler` — leaving it off one would make that endpoint the way round the rule):

- a `HouseholdMember` subject must be an **active member of THIS holding's client's household**;
- a `Client` subject must be a client **of the same tenant**, not archived or merged.

Without it any non-empty Guid was stored, and that fed a real cross-tenant leak:
`HoldingSyncService.BirthDatesAsync` reads a member's date of birth with `IgnoreQueryFilters()`
(it must — it runs under SYSTEM identity from a consumer), and that value travels into the payload
sent to an insurer. **Unguessable Guids are not an access control**, which is exactly why this repo's
rule is that `IgnoreQueryFilters` always pairs with an explicit `TenantId ==` predicate. That query
now carries the tenant AND the client id; the validator stops the bad row being written and the
predicates stop an existing one from being acted on.

It also fixes a plain correctness bug: a dangling subject reached the insurer as a beneficiary with
no name (`"—"`) on a death-benefit clause.

### A response says what the caller may do next (BNF-B-20)

`ProductHolding.AvailableActions` is computed from the SAME transition table the methods go
through, and returns endpoint names (`specifics`, `signature-request`, `signature`, `submission`,
`return-to-draft`, `closure`, `designations`). A front-end that re-implemented the state machine
would 409 the agent on a button that looked available.

Two cases do not come from the table and are commented where they are: `signature` is listed by
STATUS, because `Sign` deliberately does not move the status (re-signing is how a correction is
captured); `designations` follows `Designate`'s own refusals, because a clause is editable in every
live state except `PendingSync`.

With `HoldingDto.CanEdit`, `ClientHouseholdDto.CanEdit` and `LeadCompletenessDto.CanConvert`, each
screen of this evolution carries its own server answer. Add the flag rather than letting a client
re-derive a rule that lives in an aggregate.

### Reconciliation is two comparisons sharing one ledger (BNF-B-17)

`ReconcileTenantJob` sweeps CORE-BANKING and INSURANCE connections. They may not be merged — one
compares against `cbs_customer_snapshot`, the other against `PolicyRecord`, and reconciling either
against the other's source would report its whole portfolio as `MissingInExternal` — but they
share the ledger, the dedup, the auto-close and the run row, which is why it is one job.

- The CRM side of a holding comparison is `integration_reference` with `EntityType = Policy`, whose
  `CrmId` **is a holding id** since BNF-B-16. `ICustomersModule` exposes no enumeration and should
  not.
- `PolicyRecord` is read **narrowed by connection**, unlike the CBS snapshot which is per tenant: a
  tenant has one core banking system but may distribute for several insurers.
- **`ReconciliationScope` sets are per FAMILY, and the detected set IS the auto-close filter.**
  Passing the wrong family's set would close every gap of a type that run never looked for. A
  per-family guard test requires every `GapType` to be accounted for by every family — it found a
  real omission the day it was written.
- `GapType.DesignationOutOfSync` compares the CRM's clause version against **what was actually
  sent**, by recomputing the deterministic idempotency key and looking for a succeeded command.
  EXACT, not counted: counting succeeded commands under-reports when an intermediate avenant was
  rejected and a later one landed, which is a false finding on a contract that is up to date.
- "The insurer's beneficiaries differ from ours" is **not** detectable — nothing in `PolicyRecord`
  records the insurer's clause — and is declared undetectable rather than given a detector that
  never fires.

### The capture contract has two shapes, not two endpoints (BNF-B-18)

v1 sends `FullName`; v2 sends `FirstName`/`LastName` separately plus an optional household and
optional questionnaires, and may omit `FullName`. One record, because the shapes differ only by
which optional fields are populated — a parallel `/capture/v2` would duplicate twenty-eight fields
that must stay identical, and the day they drift the same lead scores differently by route. What v2
needed was for `FullName` to stop being mandatory: a relaxation, so every v1 body stays valid.

**`LeadNameResolver` is the one name rule for all FOUR inbound paths** — capture, web ingest,
scheduled pull, dry run. Before it the web ingest *required* `fullName` and refused a v2 form that
carried the structured pair, while the same payload pulled from a partner's API was accepted. A
supplied full name always wins: a prospect whose legal and usual names differ is ordinary here.

**The household and the questionnaires land in the SAME transaction as the lead.** A household
recorded by a second request can fail on its own, leaving a lead whose family must be re-entered —
and a health declaration in that position is worse: the consent was given and the answers are gone.

**Health answers without a consent reference are refused before anything is written**, whatever the
template says. That is a deliberate trade: a few non-health questionnaires get a 422 for want of a
reference nobody needed, but the alternative leaks which templates are health questionnaires to an
unauthenticated web form and lets a caller probe for them by submitting answers. An unknown or
unpublished template refuses the whole capture — a draft's questions can still change, so answers
against one cannot be reconstructed.

**Every `wwwroot/sdk/<version>/` must contain `forms.min.js`.** The seeder reads that exact name and
skips a version without it; the endpoint 404s for the same reason — both silently, so a build in the
tree can be unservable with nothing to say so. `ShippedSdkBuildsTests` guards it, with a named
exception list and a second test that fails once an entry is repaired. A new SDK build is always a
new version directory, never an edit: a site using `integrity` pins the exact version.

### Adding a new module

Use `Sankore.Modules.Administration` as the template: PublicApi project (interface only) + main project (DbContext + domain + Features/) + Tests project. Register in `Program.cs` with `builder.Services.Add{Module}Module(...)` and `appVersion1.Map{Module}ModuleEndpoints()`. Initialize in the startup scope if the module needs migration or seeding.
