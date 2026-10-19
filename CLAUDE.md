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

# Outbound batch files (INT-24) — the encrypted store for the files deposited at a CBS/IMF. Its
# own key: one object aggregates a whole institution's day of customer writes, so it must not be
# openable with the key that protects the command payload columns. A dev value sits in
# appsettings.Development.json; the store reads it on the FIRST batch generation, not at boot, so
# a missing key breaks a dispatch (`DispatchTenantCommandsJob ... threw`) rather than the
# start-up. Point Integration:Batch:Storage:BasePath at a mounted volume too, or configure a
# bucket — the default lands under the content root, which a redeploy destroys.
dotnet user-secrets set "Integration:Batch:Storage:EncryptionKey" "$(openssl rand -base64 32)" --project src/Bootstrapper/Sankore.Api

# Insurance claim pieces (ASS-09) — the encrypted store for pièces de sinistre. A FOURTH key,
# separate from Integration:FieldEncryptionKey, Integration:Batch:Storage and Kyc:Storage: one
# compromised key must open one concern. A dev value sits in appsettings.Development.json; the
# store reads it on the FIRST upload, not at boot, so a missing key breaks that upload rather
# than the start-up.
dotnet user-secrets set "Integration:Insurance:ClaimStorage:EncryptionKey" "$(openssl rand -base64 32)" --project src/Bootstrapper/Sankore.Api

# Call recordings (TEL-B-16) — the FIFTH encrypted store. Deliberately NOT given a dev value in
# appsettings.Development.json, unlike the four above: the recording policy ships `Disabled` and
# the store is registered ONLY when this key is present, so a deployment that never records calls
# has no key, no bucket and no player. Set it only to work on the feature — and note that losing
# it makes every stored recording unopenable, while the provider may already have deleted its own
# copy at our request.
dotnet user-secrets set "Telephony:Recordings:EncryptionKey" "$(openssl rand -base64 32)" --project src/Bootstrapper/Sankore.Api
# Point Telephony:Recordings:BasePath at a mounted volume too, or configure a bucket — the default
# lands under the content root, which a redeploy destroys.

# Antivirus for claim pieces — host:port of a clamd. UNSET by default and that is the fail-closed
# default: with no scanner, every piece is recorded ScanFailed and NOTHING is transmitted to an
# insurer. A deployment that distributes insurance must set it.
dotnet user-secrets set "Integration:Insurance:Antivirus:Address" "localhost:3310" --project src/Bootstrapper/Sankore.Api

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
transaction, and `LeadAutoDispatchConsumer` dispatches out of band — **without qualifying the
lead**, which reaches its agent in its capture status (qualifying it is the agent's work). The
dispatch then gives the agent a "Premier contact" task (`AssignmentTasks`). Never
do this inline: a lead must persist when no agent is free, and a 400-row import would otherwise
run 400 synchronous dispatches inside its loop. The consumer skips a lead captured with an owner,
a suspected duplicate, and anything already assigned — its idempotency is `CurrentAssignmentId`,
not an inbox table. It establishes `BackgroundJobContext.SetScope` **before** creating its DI
scope, because `ITenantContext` and `ICurrentUser` are built from it and a consumer has no HTTP
context.

**Dispatch queue.** A dispatch that finds no agent (no one available, all at the task ceiling,
all excluded, no licensed agent, anti-monopoly) puts the lead in the queue
(`Lead.AwaitingDispatchSince`) and publishes `LeadDispatchingFailedEvent` **once**, on entry —
both committed in a *suppressed* transaction scope, because `TransactionBehavior` rolls back a
failed command and used to take the event with it. `DispatchWaitingLeadsJob` (per tenant, every
2 min) retries the queue oldest first under the dispatching rule; any assignment clears the mark.
Without `LeadDispatchingFailed` generation rules, `LeadDispatchingFailedTaskConsumer` gives the
head of the lead's agency one "Dispatch manuel" task, closed by `AssignmentTasks` on assignment; a
lead with no agency gets no task and just waits. `DispatchingRule.MaxTasksPerAgent = 0` means no
ceiling (`HasTaskCapacityFor`) — existing rules carry 0 from their migration.

**Agent load is counted, not read.** `AgentSummary.ActiveLeadsCount`/`HotLeadsCount` come from
counters on `AppUser` that nothing updates (always 0). Every path that scores a pool — dispatch,
preview, simulation, task dispatch — passes it through `AgentLoad.WithLiveLoadAsync`, which counts
the open (and hot) leads assigned to each candidate. `LeadAutoDispatchConsumer` runs one message at
a time (`LeadAutoDispatchConsumerDefinition`) so an import's dispatches see each other's load.

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

That allow-list governs every route **except the two SDK serve routes**, which carry their own
`sankore-sdk-public` policy (`SdkEndpoints.PublicCorsPolicy`, registered by the Leads module and
required per endpoint, so it fully replaces the default one). It is the only any-origin policy in
the platform, and the snippet cannot work without it: `SnippetBuilder` emits `integrity` +
`crossorigin="anonymous"`, so the browser fetches the script as a CORS request and a customer's
own site — the only place the snippet is ever pasted — is never in the institution's allow-list.
The symptom is the browser blaming CORS for a file that is served perfectly. The policy must
never gain `AllowCredentials`: a wildcard is incompatible with it. The ingest endpoint the script
then POSTs to keeps its per-source origin check in `WebIngestCorsEndpoint` — an allow-list belongs
on the route that writes.

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

### Remote KYC collection: the link, and where its decisions live (KRM-B-01…04)

A converted customer can be sent a single-use link and deposit their identity documents from
their own phone. Lot 1 is the link itself — issuing it, sending it, re-sending it, letting it
expire — plus the per-product policy that decides whether it is sent at all. The public pages the
customer walks are KRM-B-05 onwards and do not exist yet, so a link clicked today reaches a route
the Angular app has not got.

**The policy is on the PRODUCT, the ceiling is on the TENANT, and that split is the feature.**
`ProductSpeciality.KycRemotePolicy` (typed jsonb, same converter+comparer pair as
`BeneficiaryPolicy`) says whether this line may be collected remotely, whether the machine may
approve, which tier it requires, the score floor, the accepted document types and the link
lifetime. `ComplianceSettings.KycAutoApprovalEnabled` / `KycAutoApprovalMaxTier` /
`KycAutoApprovalSamplePct` say whether the institution permits machine approval at all and how far.
Both are read by the same two modules, which is why the ceiling is in `compliance_settings` and
**not** in `kyc_settings`: `SetKycRemotePolicyHandler` has to refuse "Automatic" on a tenant that
forbids it, and this assembly cannot reference M02. A product saved as Automatic that then behaved
manually would be the one discrepancy an administrator cannot debug.

Turning the tenant switch off rewrites no product and un-approves no file — the product keeps
saying what the institution once wanted and the gate stops honouring it, which is what lets an
officer disable the feature in one click without losing a catalogue's worth of configuration.
`SetKycRemotePolicyHandlerTests` pins that.

**`KYC_AUTO_APPROVAL_ENABLED` defaults to FALSE and `PepEnabled` defaults to TRUE** — opposite
directions, both deliberate. Vigilance off by default would drop an LBC/FT obligation as a side
effect of a deployment; machine approval on by default would remove the human from a KYC circuit
nobody asked to automate. `ComplianceSettingsDefaultsTests` spells both out rather than only
comparing the facade to the entity, because that comparison passes just as well with both sides
wrong.

**The token is minted at SEND time, not at creation, and `token_hash` is therefore nullable.**
The invitation row is created at the conversion in `Scheduled` so the agent sees immediately that
a link is owed; `SendKycRemoteInvitationJob` runs minutes later, mints 32 random bytes, stores
SHA-256 of them and mails the URL. Minting at creation would mean carrying the clear token in the
job's arguments — and Hangfire arguments are rows in the shared application database and lines on
a dashboard, the two places hash-only storage exists to keep a bearer credential out of. The clear
value therefore lives inside one method, and `AttachTokenAndMarkSent` writes the hash and the
status together so there is never a walkable link nobody was told about, nor a link marked sent
that cannot be used.

The token travels in the URL **fragment** (`/kyc/verification#t=…`), which is never sent to the
server: it cannot appear in an nginx access log, in a proxy's log, or in a `Referer`. `BuildLink`
always emits `https` whatever the stored FQDN looks like — a browser refuses `getUserMedia` on an
insecure origin, so a link on http would open a page whose camera never starts for a reason no log
mentions.

**The email carries the link and deliberately NOT the client number.** The number arrives in
`client.welcome` and is the secret the link is checked against; one intercepted message must not be
enough to walk the whole flow. `KYC_REMOTE_EMAIL_DELAY_MINUTES` (default 5) exists only to ORDER
the two messages — both are queued within the same second by two different modules, and without it
the customer can reach an identification form asking for a number they have not received.

**The link is issued from `KycRequestedConsumer`, and KRM-B-03 said `KycInitiated`.** That would
not have worked: `ClientCreatedKycConsumer` usually wins the race, so `KycInitiated` is published
by a trigger that does not know the product, and the decision would read a file whose
`ProductLineId` is still null and conclude "product not eligible" for most conversions. The
product line reaches M02 on `KycRequestedIntegrationEvent` and nowhere else (KRM-B-02), so the
issue happens there — on BOTH branches of that consumer, including `AlreadyExisted`, which is the
common one. `IssueKycRemoteInvitationCommand.ReplaceActive` is false for that path and true for an
agent: a redelivered event reports the existing link and mails nothing, while an agent clicking
"renvoyer" revokes the old one, because at most one link into a file may ever be live.

**`KycFile.ResponsibleAgentId` is an ADDRESS, not a four-eyes anchor.** It is who gets told that a
remote file is waiting and who gets chased when it sits there. `LastSubmittedBy` and
`ManuallyValidatedBy` are the anchors, and a remotely submitted file will deliberately carry SYSTEM
in the first — which is precisely what leaves this agent free to decide.
`AttachConversionContext` refuses to touch a file whose `Tier` is no longer `None`: a customer
subscribing a SECOND product walks the same path, and re-pointing their validated file at the new
line would make the evidence appear to have been collected for a product nobody validated it for.

**Three things revoke a link, and the fourth that looks like it should does not.** The customer's
email changing (the credential is in a mailbox they no longer read), their date of birth changing
(the identification question no longer has the answer they were given — they would fail five times
and lock themselves out), and the file leaving collection through the back office. Every other
sensitive change — a corrected surname, a new document number, a postal address — leaves the link
valid, and revoking on those would make a routine correction cost the customer a new email.
`KycRemoteInvitationRevoker` owns the three motives and **never saves**: each caller is inside a
transaction that is about to commit the change that justified the revocation.

**M01's contact-point slices now publish `ClientSensitiveDataChangedEvent`**, which they did not
before. Nothing outside M01 cared which address was on file until M02 started mailing a credential
to it; the event already means "these protected fields changed, names only", so
`ContactPointChangeAnnouncer` reuses it rather than adding a second, contact-point-shaped event.

**`ICustomersModule` gained a SEND, not an address getter.** `NotifyClientAsync` mirrors
`IAdministrationModule.NotifyUserAsync` for the same reason: M01 owns the encrypted address, the
client's locale preference and the fallback to the tenant default, and a caller handed an address
would duplicate that fallback — a wrong locale does not degrade gracefully, PostgreSQL matches
template locales case-sensitively and a miss mails the message's own JSON payload as its body.
`ClientEmailNotifier` is the single decryption path, shared with `ClientWelcomeEmailConsumer`,
which used to carry its own copy. `HasEmailAsync` (no decrypt, for the gate) and
`GetMaskedEmailAsync` (decrypts, for the confirmation dialog) are separate on purpose: a gate
deciding whether a link can be issued must not depend on the field-encryption key being right.

**The nightly expiry sweep is not a security control.** `KycRemoteInvitation.IsUsableAt` compares
the clock, so a link is dead the moment its window closes whether or not a job has run. The sweep
makes the dead link VISIBLE — it moves the row to `Expired` so the agent's panel says "expiré"
instead of "envoyé", and it tells the responsible agent that a customer they converted never
finished.

**Why the agent's panel computes its refusal reason on every read.** The three causes — no email,
product not eligible, file no longer collecting — all change on their own. A stored reason would
keep telling an agent "pas d'email" minutes after they typed one in, and the only way out of that
is a column somebody has to remember to clear. `KycRemoteEligibility` answers the issue command and
the read with the same verdict, so the screen can never explain a cause the server did not act on.

**`kyc:remote-invite` is its own permission** and not part of `kyc:manage`: collecting evidence
inside the back office and mailing a bearer credential out of the building are not the same act.
A permission says which VERBS a caller has and nothing about which FILES they have them on, which
is why every agent-facing slice here also goes through `KycFileAccess`.

**`KycFileAccess` exists because one slice was missing its perimeter check.** The issue endpoint
shipped with `kyc:remote-invite` and no scope, reaching the same command the conversion uses — and
that one runs as SYSTEM and is deliberately unscoped. An agent in one branch holding a file id from
another could therefore have the system mail a single-use link into that branch's customer file,
and revoke the link that customer was already holding. The hole was invisible in review precisely
because its two neighbours, list and revoke, each carried their own correct copy of the twelve
lines.

The fix is structural rather than a third copy. `KycFileAccess.CanActOnAsync` answers "may this
caller act on this file" once, for the three agent-facing slices, and folds "does not exist" and
"not yours" into one `KYC_FILE_NOT_FOUND` — telling them apart is what a 403 does, and a 403
confirms another branch's file exists. The SYSTEM path deliberately does NOT go through it: the
ambient `ICurrentUser` in a conversion consumer is the converting agent, so scoping it would
silently refuse a link whenever a head-office user converts a lead for a branch customer, which
`KycFile.AgencyId` already documents as ordinary.

**That is also why there are two issue commands.** `IssueKycRemoteInvitationCommand` is the
unscoped one and stays reachable from `KycRequestedConsumer` alone;
`ReissueKycRemoteInvitationCommand` checks the perimeter and then delegates, and is the only one
the HTTP surface can reach. A boolean on a single command would have closed the hole and left the
trap — the unscoped path would still be one parameter away from any future endpoint. Use
`KycFileAccess` for a single file by id and never to post-filter a page: `GetKycFileHandler` and
`ListKycFilesHandler` apply the perimeter as a SQL predicate instead, because a list has to filter
before it counts or `TotalCount` and the page boundaries are computed over rows the caller may not
see.

### The customer's own journey: the only anonymous write surface (KRM-B-05…07)

`api/v1/public/kyc` is the first and only place in this product where somebody with no account
writes to the database. Everything below follows from that.

**`TenantResolutionMiddleware` exempts `/api/v1/public` wholesale**, so nothing upstream resolves a
tenant or compares it with the host for these routes. `KycRemoteGate` does both instead: it hashes
the presented token, probes the globally-unique `token_hash` index, reads the tenant off the ROW,
and refuses when an `X-Tenant-Fqdn` resolves to a different tenant. Without that last check a link
leaked from one institution would open under another institution's logo and the customer would type
their client number into it. An ABSENT header passes — the token is the credential, and refusing a
request that merely did not say which domain it came from breaks every non-browser client.

**Every public endpoint re-reads the invitation, and that is what "the session is revoked" means.**
A signed JWT cannot be withdrawn, so a session is never trusted on its signature alone: an agent
revoking a link, or the customer submitting, stops the session on its very next call — no
revocation list, and no 20-minute window in which a spent link still writes.

**The link's window is read off the CLOCK, never off the status.** `Expired` only appears when the
nightly sweep runs; trusting it would leave a bearer credential working for up to a day past its
stated lifetime. `KycRemoteInvitation.IsUsableAt` is the authority and the sweep is cosmetic.

**The session token is a second JWT identity** — its own scheme (`KycRemoteSession`), audience
(`sankore-kyc-remote-session`) and signing key, plus a `scope=kyc:remote` claim the policy requires.
Three independent reasons a token issued for anything else is refused. The key is **derived** from
`Jwt:SigningKey` with HKDF-SHA256 and a versioned info string rather than being a new secret:
requiring one would fail every existing deployment on the upgrade that merely adds the feature, and
whoever holds the platform key can already mint an administrator's token, so derivation is not the
weak link. `Kyc:RemoteSession:SigningKey` overrides it for an operator who wants independent
rotation. See `KycRemoteSessionOptions`.

**The identification endpoint answers ONE failure for everything.** Wrong number, wrong date, both,
a client with no recorded date of birth: all `401 KYC_REMOTE_IDENTIFICATION_FAILED`. Telling them
apart would make it an oracle — "that client number exists" is the half an attacker cannot get
anywhere else, and from there a date of birth is a few thousand guesses. The number of remaining
attempts is deliberately not returned either.

**The guessing bound is the per-link counter, not the rate limiter.** The `kyc-public` policy is per
IP, in memory, and resets on every deployment — it bounds a flood. `FailedAttempts` on the
invitation is durable and per token, and locks the link for good at
`remote-max-identify-attempts`. A SUCCESSFUL identification resets it: a customer who mistyped three
times and then got it right has not burned three of their five tries for the rest of the link's
life.

**`ICustomersModule.VerifyRemoteIdentityAsync` returns a boolean and takes the question, not the
answer.** The client number and the date-of-birth blind index are M01's; the comparison happens in
`RemoteIdentityVerifier`, compares the date on its HMAC index rather than decrypting, and runs both
comparisons with `CryptographicOperations.FixedTimeEquals` combined with `&` and not `&&` — so
"wrong number" does not return measurably faster than "right number, wrong date". Whitespace and
casing are forgiven; the format's own separators are not, because stripping them collapses every
tenant's format into one digit string.

**The public endpoints are thin endpoints, not MediatR slices, and that breaks the repo
convention deliberately.** `AuditBehavior` serialises a command's whole payload into the audit
trail, and these payloads are a client number, a date of birth, and photographs. The identification
attempt is still audited — with an empty payload, which is the entire point. The one write that
genuinely belongs in a command is the verification, dispatched from the submission with storage
references and no bytes.

**The quality gate is a gate, not the evidence.** KRM-B-06 asked for the upload-time OCR reading to
be kept and reused at submission; it is not. The authoritative reading runs through
`RunKycVerificationCommand`, which is the only thing that writes `KycIdentityDocument` — reusing the
upload-time one would bypass the field corrections, the duplicate detection and the scoring the
command performs around it. The cost is one extra OCR call per document; the benefit is one path
from a photograph to a verified identity. An unavailable service ACCEPTS the photograph, and past
the tenant's retake budget the gate stops refusing: a customer whose camera the service dislikes
must never be stranded with no human to ask.

**A selfie is quality-checked through the face-match call and the verdict is discarded.** It is the
only way to ask "is there exactly one face here", which is why the identity document must be
uploaded first. Only `NO_FACE_DETECTED` and `MULTIPLE_FACES_DETECTED` reach the customer; any other
rejection is about the document, and refusing the selfie for it would send them retaking the wrong
photograph.

**`uploaded_by` is the INVITATION id, never a user.** An employee recorded as having uploaded the
image would be barred from deciding on the file by the four-eyes rule, and no employee touched it.
The same reasoning puts SYSTEM in `LastSubmittedBy` at submission — which is exactly what lets the
remote path route to one human instead of two.

**`KycChannel.RemoteSelfService` moves at SUBMISSION**, not when the link is sent: a link nobody
used is a file waiting at a counter, not a remote enrolment. Adding that value also moved the
"which channels are remote" predicate into `KycChannels.IsRemoteSale` — spelled as
`Web or MobileAgent` in two places, the newest and most remote channel of all would have fallen
outside both, and CIMA article 13.6's proof-of-address gate would have stopped applying to the only
channel where no employee ever saw the document.

**The consent text is compiled in, versioned, and served by the API.** What has to be provable years
later is the exact wording this person agreed to; a row an administrator can edit cannot answer
that, because editing it once makes every past consent point at text nobody consented to. A stale
version is refused with `KYC_REMOTE_CONSENT_VERSION_STALE` so the page reloads rather than recording
agreement to wording it never displayed.

**`KycDocument.DeclaredDocumentType` exists for this journey.** On the agent path a human passes the
document type at verification time; on the remote path nobody is there when the file is submitted,
so the customer's choice has to live on the image they made it for. Reading it off the newest
front is also how a customer corrects a wrong choice — they retake the photograph.

Front-end counterparts worth knowing: the Angular route is `kyc/verification`, declared **before**
the guarded `kyc` route (declared after, `authorized` would capture it and the emailed link would
never have worked), the token is read from `location.hash` and wiped with `replaceState`, the
session lives in memory only (`KycRemoteSessionStore` — a refresh sends the customer back to
identification and KEEPS their photographs, which are server-side), `public/kyc/` is excluded from
`accessTokenInterceptor` so a customer's expiring session cannot trigger a `logout()` that signs out
the agent working in another tab, and `KycCameraCapture` gained an `audience` input that changes the
camera-unblocking instructions only — "click the padlock left of the address bar" and "plug in a
webcam" are support calls when said to somebody on a phone.

### Who decides a remote file, and the one gate that makes the feature almost unreachable (KRM-B-08…10)

A remotely collected file is routed once its verification comes back: either the product's policy
approves it with **no human at all**, or a human is asked and told why. Lot 3 is that decision, the
notification that follows it, and what the customer is told.

**`KycAutoApprovalPolicy` is a pure function over a record, and every compliance condition lives
there.** No DbContext, no module contract, no clock: `RouteRemoteKycFileHandler` gathers the facts
and the policy weighs them. That is what makes the specification's own definition of done —
"règles d'approbation automatique couvertes par des tests unitaires cas par cas" — a sentence a
unit test can satisfy, instead of a scenario needing a scored file, a product, a tenant and a mail
server. A condition implemented in the handler would be one no test could reach.

**It only ever approves.** There is no verdict that refuses a customer, including on the worst
possible evidence: a `Rejected` score goes to an agent who chooses between a complement and a
refusal. That asymmetry is the whole compliance argument for the feature — the automatic path can
only skip a signature the institution would have given anyway.

**Three places the specification cannot be followed literally**, each documented on
`KycAutoApprovalPolicy.Evaluate`:

| §3.2 says | What exists | What is done |
|---|---|---|
| "Niveau de confiance **Validated**" | `KycConfidenceLevel` is Rejected/Low/Medium/High; the service grades Low/Medium/High | read as `High` **and** the product's score floor on top. Reading it as "anything but Rejected" would let a Low-confidence file through on a score |
| "aucune **alerte LBC-FT** ouverte" | no AML-alert entity anywhere | an OPEN event-driven `KycReviewSchedule` plus the file's own `DuplicateSuspected`, folded into `HIGH_VIGILANCE` and `BLOCKING_FLAG` so the reason list stands |
| five **blocking flags** | strings from the scoring service, stored verbatim | `KycBlockingFlags`, matched case-insensitively. A flag the list does not know is NOT blocking — widening it is a decision somebody takes, not something a service deployment does overnight |

**Two reason codes beyond the specification's list, and both had to exist.**
`AUTO_APPROVAL_DISABLED`, because KRM-B-01 refuses to *save* a product in automatic mode while the
tenant switch is off — so the state is only reachable by switching it off afterwards, which is
exactly when an officer needs to see it bite, and the nearest listed code would send them to edit
the wrong screen. And `PROOF_OF_ADDRESS_REQUIRED`, which is the consequential one:

> **CIMA article 13.6 makes an automatic approval nearly unreachable today, and that is deliberate
> rather than a bug.** `RemoteSaleEvidence` refuses the FINAL approval of any remote file without a
> proof of address that is `Accepted` and carries an issue date inside the tenant's window — and
> `RemoteSelfService` is the most remote channel there is. "Accepted" is a reviewer's decision and
> the issue date is a reviewer's entry, so until somebody at the institution reviews a customer's
> utility bill, no remote file of a product subject to 13.6 is approved by the policy: it routes to
> a human under this code. The gate is called from the routing precisely so the automatic path can
> never grant a tier the manual path would have refused on the same file — the one way this feature
> could be worse than no feature. Whether the remote journey should collect the issue date from the
> customer and the policy accept an unreviewed proof is a **compliance decision for each
> institution** and is not taken in code.

**`KycFile.ApproveAutomatically` exists because `Approve` cannot be used.** `Approve` refuses
whoever `LastSubmittedBy` names, and a remotely submitted file deliberately names SYSTEM there —
so `Approve(tier, Guid.Empty, …)` is refused by the four-eyes rule every time. That refusal is
correct and stays: SYSTEM approving what SYSTEM submitted is exactly what the rule describes. An
unattended approval is therefore a different act, named differently, so the audit trail says what
happened instead of showing a signature nobody gave. The method re-checks the channel itself: it is
the single place in the module that grants a tier with no human, and "the caller already checked" is
not good enough there.

**An automatic approval must be indistinguishable from a manual one downstream**, and a test pins
each half: same tier, same status, the same `KycValidatedEvent` + `KycTierChangedEvent` through the
same outbox, and the same *absence* of a scheduled periodic review — because the manual path does
not schedule one either (nothing calls `KycReviewScheduler.SchedulePeriodicAsync`, a pre-existing
gap). A path that scheduled one would be a divergence in the other direction. `KycRemoteDecidedEvent`
is published on BOTH outcomes and is the only thing that says *who* decided; the two existing events
say nothing about how, and must not.

**`RunKycVerificationHandler` no longer opens the circuit for a remote file.** The router owns it:
either the single already-decided `Automatic` step, or the human ladder through
`StartKycApprovalCommand`. Writing the ladder first would mean the automatic branch had to unwrite
it, and deleting approval steps is the one thing a circuit must never do — a routine that removes a
pending step is a routine that can remove a signed one. The cost is a window where a `Validating`
remote file has no circuit until the outbox delivers; nobody is sitting on that screen, because
there is no agent behind a remote submission.

**`KycApprovalLevel.Automatic = 90`, specifically not 0.** Zero reads as "below the agent", which is
the honest description — and would make it `default(KycApprovalLevel)`, so a deserialisation that
lost the field would say a file was approved automatically. For a compliance enum that is the one
default nobody can afford.

**Idempotence is `KycFile.ValidationRoute`.** M02 has no inbox table, the trigger is a broker
message, and a second evaluation would either mail the agent twice or re-approve a file an agent has
since sent back. `RouteToManual` refuses to overwrite a route; the automatic branch is guarded by
the status transition itself. `ClearRouting` is called when a file re-enters verification so the
NEXT round is routable — without it the idempotence guard would silence the complement the customer
just sent. It deliberately cannot clear `Automatic`.

**`KycFile.RoutedAt` is its own column and not a reading of `UpdatedAt`.** KRM-B-09 chases the
responsible agent N hours after the file was handed to them, and `UpdatedAt` moves every time
somebody opens the file — measuring from it would reset the promise on every look, which is the
opposite of what an SLA on a decision means.

**The recipient cascade is M12's, and its third rung does not exist.**
`IAdministrationModule.ResolveAgencyRecipientAsync` answers the responsible agent when they are
`Active` **and** still attached to that agency, else the agency's manager (also checked for
`Active`), else null. The specification's "sinon la file de l'agence" has nothing to address — this
platform has no agency distribution list — so the cascade stops rather than inventing a recipient,
and null is an ordinary answer every caller handles: the file is in the back-office backlog either
way. Both legs matter: a disabled account still has a mailbox the provider accepts, and an agent who
changed branch must stop receiving their old branch's work.

**Every email is sent from an outbox consumer with an idempotency key**, which §8 asks for in those
words: `QueueEmailAsync` is another module's table and is not part of M02's transaction, so a mail
queued inside the routing would be sent for a decision that then rolled back. The keys are shaped
for what a legitimate second message is — `kyc.remote-review-required` is keyed on
`(file, routing reason)`, so a complement that re-routes the file can notify again while a
redelivery stays silent; the overdue chase adds the recipient id, because a shared key would let the
agent's copy silence the manager's.

**The customer's approval email is identical whether a machine or a human approved.** KRM-F-05 makes
that a requirement of the customer's own screen, and it would be odd for the email to then give the
mode away — they have no action to take either way. The refusal carries **no motive at all**: the
approver's comment travels on `KycRejectedEvent` and is dropped here, because it is written for
operators and some refusals must not be explained (telling somebody which check they failed tells
them what to change before trying again). `agency_phone` renders empty — no telephone number exists
on an agency or on the institution anywhere in this platform, and the variable is in the template so
adding the field later needs no template migration.

**A complement link is scoped, and the scope also re-opens the journey.**
`KycRemoteInvitation.RequestedKindsJson` holds the kinds the approver listed; `KycRemoteJourney`
then requires exactly those — not even the front and the selfie an initial link always collects —
and counts one as present only when it was uploaded *after* the invitation was created. Without that
second half the journey would open with nothing missing and the send button would resubmit the very
image that was refused. The old evidence stays on the file, where it belongs. An EMPTY scope means
"the whole journey again", which is the reading that cannot strand anybody. The scope is validated
against the product's policy **before** the decision is applied, not when the link is issued: the
link is issued best-effort after the commit, so a refusal there would be a log line the agent never
sees.

**`ICustomersModule.MatchesReadIdentityAsync` is asymmetric on purpose.** Every given name on the
RECORD must appear among those READ on the document, not the reverse — a card lists a person's full
civil given names and a counter capture routinely holds fewer, so the strict direction would route
nearly every file to a human and the condition would stop discriminating. The surname is compared
whole. An INCOMPLETE reading is NOT a match: a document whose surname the extractor could not
produce is not a document that agrees with the record, it is a document nobody checked. Normalisation
is `SearchKeyBuilder.NormalizeTerm`, which already defines "accent-free, upper-cased, punctuation
collapsed" for client search — which is exactly what §3.2 asks for.

**`KycOcrFields` was extracted so the field-name matching has one body.** The verification writes
`OcrFieldsJson` and the routing reads it back; two private copies of `Simplify` would mean two
opinions on whether `given_names` and `givenNames` are the same field, and the routing copy is the
one nobody looks at — a service rename would silently make every remote file fail its identity check
and route to a human. A failure in the safe direction is still a failure nobody would diagnose.

Front-end counterparts (KRM-F-08): `KycRemoteDecisionBanner` sits at the TOP of the decision screen,
before the status card — a validator opening a remote file reads something different from a counter
file, and the reason the machine did not decide orients their whole reading; placed lower it would be
read after the score, which is too late. It renders nothing for a counter file. `kycRoutingReasonLabel`
is the only translation of the reason codes and a code it does not know becomes a generic instruction
rather than an identifier under a branch manager's eyes — `kyc-routing-reason.spec.ts` pins exactly
that, the same property `kyc-remote-labels.spec.ts` pins on the customer's side. `KycApprovalLevel.Automatic`
is labelled "Validé automatiquement" and NOT given a validator's name, so the history cannot read as
though a person signed. The retake-document checkboxes only appear for a remote file, because on a
counter file the agent fetches the better photograph themselves and the list would have nowhere to go.

### Controlling what the machine decided, and the second factor that cannot be delivered (KRM-B-11/B-12, F-09)

**`kyc_auto_approval_controls` is the price of the automatic mode, and it is a table rather than an
assurance.** Every other KYC file in this platform passes under at least two pairs of human eyes;
an automatic approval replaces both with the SYSTEM account. The specification's own compliance
note says what that costs — a sampled after-the-fact control — and this is it. Its own entity and
not a flag on `KycFile`, for three reasons and the third decides it: a file can be approved
automatically more than once over a lifetime (a review re-opens it) so a column would be
overwritten; the queue is read by a different person under a different permission; and the verdict
carries its own author, date and finding, which is evidence in its own right — a regulator asks who
checked, not only whether somebody did.

**The draw is random and deliberately NOT a hash of the file id.** A deterministic draw would be
reproducible, which is attractive for an audit, and also PREDICTABLE — anybody who can influence
which id a file gets could keep their approvals out of the sample, and a control that can be steered
is not one. Reproducibility comes from the rows instead: an institution answers from the table, not
by re-running a function. `RandomNumberGenerator` rather than `Random.Shared` because nothing in the
process can seed or replay it.

**It is drawn AT the approval, inside its transaction.** The sample is of APPROVALS: a nightly job
picking rows would sample the approvals that still exist and still look automatic, which is a
different and quietly shrinking population. `KycAutoApprovalSampler.Draw` adds the row and the
CALLER saves — a crash between them would leave either an approval nobody will ever control or a
control of an approval that never happened. It never throws and never fails the approval: a
compliance control causing the harm it exists to detect is the one failure mode it may not have.

**`PolicyVersion` is on the row because the rules are code.** The question is "under which
conditions was this relationship entered into without a human", and an approval from last quarter
was taken under a `KycAutoApprovalPolicyVersion` the service no longer runs. Bump that constant when
a condition is added, removed or loosened — never for a wording change.

**`kyc:auto-approval:control` is its own permission, not `kyc:approve`.** An approver decides whether
to enter into a relationship; this one answers to a regulator for the institution having let a
machine decide at all. Granting it to every approver would mean the people whose signatures the
automatic mode replaced are the ones auditing its replacement.

**The queue read has NO agency perimeter**, the only KYC list that does not, and the permission is
what makes that safe: a queue narrowed to one branch answers "did MY branch let machines decide"
while leaving the real question, about the tenant, unanswerable.

**The four-eyes rule this feature can enforce is about the FILE, not the approval.** An automatic
approval was signed by SYSTEM, so `LastSubmittedBy` is useless as an anchor; the guard refuses the
file's own `ResponsibleAgentId` instead — the person whose conversion produced the file must not
certify that a machine was right to validate it unattended. It answers 403 and not 404 here, unlike
everywhere else in this module: the caller can already see the row, so there is nothing left to
hide, and "not found" would send an officer looking for a row in front of them.

**A non-compliant verdict raises its review AFTER the commit and its failure does not undo the
verdict.** A control recorded without its review is a true statement about what happened and leaves
a visible row; a verdict rolled back because a review could not be scheduled would lose the finding,
which is the one thing that cannot be reconstructed. The response carries `raisedReviewId: null` so
the screen can say "raise one by hand" instead of hiding the gap. The review's reason is a FIXED
sentence and not the officer's finding — the two have different audiences.

**KRM-B-12's SMS second factor ships complete except for its delivery, and the switch fails
closed.** This platform has **no SMS channel at all**: M08 is email only — `EmailTemplate`,
`QueueEmailAsync`, SMTP, Brevo — and there is no provider, no queue, no per-tenant credential and no
delivery receipt for a text message anywhere in the solution. The story lists « M08 SMS » as a
dependency that does not exist, and building it is a module-sized piece of work, not a clause of a
Could-Have story.

So the mechanism is real and tested — six digits, SHA-256 salted with the invitation id, a
ten-minute window, three tries, one-time consumption, a bounded resend — and the delivery is
`IKycOtpSender`, whose only registered implementation is `UnavailableKycOtpSender`.
**`UpdateKycSettingHandler` refuses to turn `remote-otp` on while that sender is unavailable**, the
one setting in this module whose validity depends on what the host registered rather than on its
own value (which is why the check is not in `KycSettingRanges` — a range validator cannot see the
container). Turning it on with no channel would not degrade the journey, it would END it: every
customer of that tenant would reach a code screen for a message that is never sent, with nothing in
the logs to say why. The day a provider lands it implements the port and replaces one registration.

Three properties of the OTP worth knowing before touching it. The hash is **salted with the
invitation id** — a six-digit code has a million values and an unsalted SHA-256 of all of them is a
table somebody builds once, so a stolen column must be a million hashes per row rather than a
lookup. A **malformed entry costs no attempt**: `HashOrNull` shape-checks before hashing, so a blank
or four-digit entry cannot burn somebody else's three tries, and a public endpoint cannot be made to
hash an arbitrary string. And a new code resets the code's own counter but **never** the per-link
`FailedAttempts` — that one bounds guessing at the client number and date of birth, and a resend
must not buy an attacker five fresh tries at the first factor. A code that could not be SENT is not
a session: falling back to opening one would switch the tenant's second factor off at the exact
moment the channel failed.

**`KycOtpVerdict` has four values and the endpoint shows one.** The aggregate distinguishes "no code
pending", "expired", "too many attempts" and "wrong" because they call for different instructions;
the public surface answers a single `KYC_REMOTE_OTP_INVALID`, because telling them apart says whether
a live code exists for a link somebody holds. The verdict is LOGGED — an operator diagnosing "nobody
can get through" needs to know whether codes are stale or wrong — and never returned.

**nginx (§8).** `log_format sankore_public` is defined without `$request_body` and without
`$http_referer` and applied to `^~ /api/v1/public/kyc`: the default format carries neither, but
nothing stops a deployment from copying one that does, and under that prefix the body is a client
number, a date of birth and an SMS code. The `Referer` goes for the reason the token is in the
fragment — a fragment never reaches the server, but an intermediate page could put the whole URL in
a header, and a log is where a string survives for months. HSTS is emitted only when
`$http_x_forwarded_proto` is https (TLS terminates upstream, so this server listens on 80 and cannot
require it, only make the browser require it) — unconditional, it would make a local HTTP deployment
unreachable for a year on any machine that saw it once. `map` is an `http`-context directive: in a
`server` block nginx refuses to start, which is why it sits at the top of the file and not next to
the `add_header` that reads it. Verified with `nginx -t`.

**KRM-F-09 is a proposition and never a barrier**, because the detection is a heuristic and says so.
The only reliable test is `getUserMedia({video:{facingMode:{exact:'environment'}}})`, which raises
the camera permission prompt — asking somebody on a desktop for camera access in order to tell them
to use their phone is the opposite of the point. `enumerateDevices` without permission returns
entries with empty labels, so neither `label` nor `facingMode` is readable and what remains is the
COUNT: two or more video inputs means a phone, one on a touch device means a tablet. A laptop has a
webcam and no touch screen and lands on "no rear camera"; a touch-screen desktop with one webcam is
the documented false negative and goes straight to capture, which is the less annoying of the two
outcomes. An API that is absent or refuses counts as "continue here" — a browser that will not
answer must not send every customer to a QR code.

**The QR encodes the full link, token included**, which is what makes the screen useful: scanning it
opens the same page on the phone, identification included. The token therefore reappears on screen,
so the component writes it nowhere — the parent rebuilds the URL from the token held in MEMORY,
because `_readTokenFromFragment` wiped the address bar. The link stays single-use server-side:
scanning moves the journey, it does not duplicate it, and the desktop's session stops working on its
next call because `KycRemoteGate` re-reads the invitation every time. `qrcode-generator@2.0.4` is a
new dependency (zero transitive deps, MIT, one lockfile entry). It was installed with
`--legacy-peer-deps`, which the repo required at the time; it no longer does — `ngx-quill` has moved
from 30.1.3 (peering on Angular 21 against a workspace on 22) to 31.0.1, which peers on 22, so
`npm ci` and `npm install` both run with no flag. The SVG is drawn by hand and
`kyc-continue-on-phone.spec.ts` pins the part that was written here rather than the encoder: one
rect per dark module, `x`/`y` as (column, row) — a transposed QR looks identical and does not scan —
and the four-module quiet zone, which is part of the standard and not decoration.

### The SMS channel: a second transport beside the email one, not a variant of it (M08)

A tenant can configure **Brevo** or **Twilio** for text messages, with its own credential, its own
sender identity and its own monthly allowance. Everything mirrors the email channel's shape so one
mental model covers both — and the places it deliberately does not are all downstream of one fact:
**an email costs almost nothing and a text message is billed per unit.**

| | Email | SMS |
|---|---|---|
| Tenant settings | `administration.tenant_notification_settings` | `administration.tenant_sms_settings` |
| Vault secret | `{provider}-credential` | `{provider}-sms-credential` |
| Templates | `notifications.email_templates` | `notifications.sms_templates` |
| Queue | `email_outbox_messages` | `sms_outbox_messages` |
| Delivery log | `email_delivery_logs` | `sms_delivery_logs` |
| Resolver cache key | `notifications:provider:{t}` | `notifications:sms-provider:{t}` |
| Change event | `TenantNotificationSettingsChangedEvent` | `TenantSmsSettingsChangedEvent` |
| Processor | 30 s, batch 50, 3 attempts | **10 s, batch 20, 2 attempts** |
| Platform fallback | the platform's SMTP account | **none — no channel means no send** |

**Separate tables rather than a `Channel` column, every time.** One settings row mixing both would
make `UpdateProvider`, `ResetToDefault` and `TryConsumeMonthlyQuota` silently mean "email" — which
is how a quota gets consumed by the wrong channel. One outbox would mean every email column
nullable, two processors competing for the same hot rows, and a channel filter whose absence sends
an SMS through the email sender and reports it delivered. One template row would carry a meaningless
`Subject` and plain text in a column called `HtmlBody`.

**`{provider}-sms-credential` is a different secret NAME from the email one, and that is not
cosmetic.** Brevo sells both channels, so a tenant can legitimately run Brevo email and Brevo SMS at
once — with a shared name, storing the SMS key would silently overwrite the email key. The cost is
asking an administrator who uses one Brevo key for both to type it twice; the alternative loses a
working email configuration with no error.

**The two allowances are separate counters, and that is the point.** A month of marketing emails
must not exhaust the allowance a customer's one-time code needs. `TryConsumeSmsQuotaAsync` also
differs from its email twin in one place: a tenant with **no row is refused** rather than granted.
For email, no row means "no limit configured" and the platform account sends it anyway; for SMS it
means there is no provider and nobody paying, so granting an allowance would hand the processor a
message it is about to fail on — dead-lettered under a network error instead of under
`CHANNEL_NOT_CONFIGURED`.

**`TenantSmsSettings.CanSend` is computed once, on the entity, and every other module carries it.**
Four conditions — provider chosen, sender identity set, calling code configured, credential stored
(plus an Account SID for Twilio) — and each one alone produces a channel that looks configured and
sends nothing. M08's resolver copies the flag rather than re-deriving it, and so does M02: a second
derivation is a second chance to get one of the four wrong, and wrong in the permissive direction
locks a tenant's customers out of their own journey.

**`DefaultCallingCode` exists because nothing else in this platform knows the country.** A tenant
has a name, a logo and a default language and no country; M01 stores numbers as they were dialled,
because `SensitiveValueNormalizer.NormalizePhone` deliberately infers no indicatif — inferring one
wrongly makes two different subscribers collide in a blind index. So a customer captured at a
counter in Abidjan is `0708091801` and a provider needs `+2250708091801`, and the bridge is a
per-tenant setting. **Required to enable the channel**: an institution whose customers are stored in
local form would otherwise have a channel that silently cannot reach most of them.
`ClientSmsNotifier.ToE164` is `internal` rather than private so that conversion — the one place
where being wrong sends a code to a stranger — has cases of its own. The documented limitation:
a LOCAL number from a trunk-zero plan (Ghana, Nigeria, France) keeps a zero it should lose. Accepted
knowingly — the tenants are UEMOA institutions whose plans have no trunk prefix, and stripping a
zero that is significant in Côte d'Ivoire corrupts the common case to fix the rare one.

**`SmsSegments` is the one piece of arithmetic that shows up on an invoice rather than in a log.** A
GSM-7 message fits 160 characters; a single character outside that alphabet — one `œ` — switches the
whole message to UCS-2 and drops the budget to 70, turning one segment into three. `body.Length /
160` would report one and the provider would bill three. Note the trap the tests pay for: **`é` IS
in GSM-7** (so are `è ù ì ò à ä ö ñ ü Ç`), so writing that case with `é` asserts nothing. The
extension characters `^ { } [ ] ~ | € \` cost two septets each, and UCS-2 bills UTF-16 code units
so an emoji above U+FFFF costs two. **The seeded French bodies carry no accents** — "verification",
not "vérification" — which looks like a typo in `SmsSeeder` and is a cost decision; the seeder logs
a warning when a platform body exceeds one segment and a test fails the build.

**A missing template dead-letters instead of falling back.** `ScribanTemplateRenderer` renders the
raw payload as a `<pre>` block and lets the email go, which is recoverable — somebody receives an
ugly email. `SmsTemplateRenderer` reports `Found: false` and the processor refuses: the same
behaviour here would spend a tenant's money to send a customer a JSON fragment. Same reason the
composite sender has **no stub fallback**: an unknown provider must fail loudly, because the
alternative on this channel is silence that still costs money somewhere.

**The destination is redacted on every terminal state.** `SmsOutboxMessage.RecipientPhone` holds a
clear E.164 address because the processor needs it minutes later in another process — a queue it
cannot read is a queue it cannot send — and `Redact` replaces it with the masked form once the
message can no longer be sent. The mitigation is lifetime, not secrecy.

**`Sent` means the provider accepted it, never that the handset received it.** That second question
is `ProviderStatus`, which a delivery callback fills in later, and conflating them is how a platform
reports a 100% delivery rate on messages nobody got. A callback therefore **never** moves a message
back out of `Sent`: rewriting the status would make the processor pick the row up and send a second
one — a customer receiving two codes because the first was reported undelivered.

**The two providers verify their callbacks differently, and that asymmetry is theirs.** Twilio signs
with the account's Auth Token (HMAC-SHA1 over the URL plus the alphabetically sorted form body,
base64) — so the tenant is resolved from the `AccountSid` the payload carries, through
`IAdministrationModule.ResolveTenantBySmsAccountSidAsync` and **not** a cross-schema query, and the
token is then read from the vault. Brevo does **not** sign SMS events at all; its documented
protection is IP allow-listing, which a multi-tenant platform behind a load balancer cannot rely on,
so its URL carries a per-tenant token derived with HKDF from `Jwt:SigningKey` — no new secret, no
rotation story, and the same argument `KycRemoteSessionOptions` makes. Two things to know: **the
Twilio signature must be computed over the URL Twilio built**, which behind a terminating proxy is
not the one the application sees (`ExternalUrl` reconstructs it from the forwarded headers — this is
the most likely reason a correct integration reports every callback as forged); and an **unknown
account answers 204, a bad signature answers 403** — the first is a stale provider setting or
somebody else's traffic and erroring makes the provider retry for hours, the second needs to be
visible.

**An unrecognised provider status word is `Accepted`, not `Undelivered`.** Both providers emit
intermediate states and a future one will emit a word the list does not know; reading that as a
failure reports non-delivery for messages still in flight, which produces false support tickets
rather than missed ones.

**KRM-B-12's second factor is now switchable, and only by a tenant that can actually send.**
`IKycOtpSender.IsAvailableAsync` is per TENANT (it was a static `IsAvailable` when the mechanism
shipped without a transport) and reads M12's `CanSend`; `UpdateKycSettingHandler` refuses to turn
`remote-otp` on while it says no, so an administrator learns it instead of their customers.
`SmsKycOtpSender` goes through **M01's** send-to-client contract rather than straight to M08,
because the number is M01's secret and the E.164 conversion needs the tenant's calling code. Its
idempotency key is the client plus the code's own ten-minute window: keyed on the invitation alone a
customer asking for a second code would be silently given none, keyed on nothing a retry would
charge twice. The message carries **the code and the window and nothing else** — a text message is
readable on a locked screen, and naming the institution and the customer beside a code is how a
stolen handset becomes an enrolment.

Front-end: `sms-parametrage` is its own screen under `settings/notifications/sms`, not a section of
the email one — the two share nothing and a tenant runs one without the other. Three things it shows
that only the server knows: `canSend` (the difference between configured and half-configured),
`senderIdHint` (the rule belongs to the provider, and a hard-coded "11 characters maximum" is wrong
the moment somebody switches to Twilio), and the callback URL to paste into the provider. The
provider list comes from `availableProviders` on the DTO so the options cannot drift from the
server's own list. `qrcode-generator` aside, no new dependency.

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
| `beneficiary_policy`, typed jsonb | `AllowedRoles`, `MaxChildren`, `ChildMaxAge`, `RequiredFieldsBeforeConversion`, `RequiredKycTier`, `IntegrationSystem` | optional fields M13 and M01 read **whole** and never filter on |
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
| `administration.tenant_notification_settings` | typed, one row per tenant | M12 | M08 |
| `administration.tenant_sms_settings` | typed, one row per tenant | M12 | M08, M01, M02 |

`compliance_settings` is typed and not key/value because its values have real invariants (a GIIN has
a fixed 19-character structure, and enabling FATCA without one is a half-configured regulatory
obligation) — a `Value string` column pushes both rules into a parser every caller must remember to
run. It sits in M12 and not M13 because three modules read it: a `leads_settings` row would make M01
and M02 depend on M13 to learn whether their own institution is subject to FATCA.
`IAdministrationModule.GetComplianceSettingsAsync` **never returns null** — a tenant with no row
runs on the platform defaults, and returning null would hand every caller the same question to
answer. `AdministrationModuleFacade.PlatformDefaultCompliance` duplicates
`ComplianceSettings.CreateDefault`, and `ComplianceSettingsDefaultsTests` pins the two together.

**FATCA and PEP are both tenant switches, and the second one should not have been.** FATCA depends
on the country's intergovernmental agreement or on the institution's own IRS registration, so it is
a setting by nature. PEP vigilance follows the LBC/FT rules binding every UEMOA institution, and
this file used to say that nothing turns it off. `ComplianceSettings.PepEnabled` was added on the
product owner's explicit decision, against that reasoning and knowing it — the front end had
shipped a per-tenant toggle. The argument is kept on the domain property rather than deleted.

What the switch governs is **collection**: with PEP off, a lead is no longer blocked for an
undeclared exposure (`LeadCompletenessCalculator` skips `PEP_STATUS` entirely, so the percentage
does not count a question nobody is asked) and the front offers no PEP block. It does **not** erase
exposure already recorded, and it does **not** stop M01 or M02 recalculating exposure from data
already captured. Default **true** everywhere — factory, platform defaults and column — because a
default of false would switch off an LBC/FT obligation for every existing tenant as a side effect
of a deployment. An outage reading the flag also answers true: an outage must never relax a rule.

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
the product line's household ceilings. Enrichment uses the **first**, because resolving the policy
costs an M12 call and brings `PRODUCT_POLICY_UNAVAILABLE` with it — a write that never consults a
ceiling should not fail for want of one. Two methods rather than a boolean: a gate flag is one a
call site eventually passes the wrong way round.

**No product line refuses a household, and none can.** A household is a fact about the prospect,
not a feature of the product they are asking about: the agent records the family once, whatever the
request is later qualified as. `HouseholdPolicyResolver` therefore reads only `MaxChildren` and
`ChildMaxAge` — ceilings bound a household the product will carry rather than deciding whether one
exists.

`BeneficiaryPolicy.HouseholdEnabled` is **gone from M12 entirely** — the jsonb record, the
`BeneficiaryPolicyDto`, `SetBeneficiaryPolicyRequest`, and the "Activer le foyer" checkbox in the
product settings editor. It answered `HOUSEHOLD_DISABLED_FOR_PRODUCT_LINE` and defaulted to
**false**, so every line whose policy was saved without ticking it refused a spouse nobody had meant
to forbid. Two more things fell out with it:

- `BeneficiaryPolicy.Create` no longer blanks the ceilings. It used to keep them only when the flag
  was set, so "at most 3 children" typed without ticking a separate box was silently discarded —
  the ceilings are now reachable on every line.
- The front gates are gone: `LeadHouseholdDto` has no `HouseholdEnabled`, and M01's
  `designations-editor` shows "add to household" on every line. That one never had a server
  counterpart at all — `DesignationSubjectValidator` only checks that a subject is an active
  household member or a client of the tenant.

Old jsonb rows keep a dead `householdEnabled` key; the converter reads with
`JsonSerializerDefaults.Web`, which skips unmapped members, so no migration is needed and the key
disappears the next time a policy is saved.

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

**Each version directory holds `forms.js` (readable source) and `forms.min.js` (what sites load and
pin).** From 1.2.1 on, the minified file is GENERATED: edit `forms.js` in a new version directory,
bump its `SDK_VERSION`, then `npm run sdk:minify -- <version>` (terser, from the repo root) — it
prints the SRI hash and refuses to overwrite an existing `forms.min.js`. Builds 1.0.0 to 1.2.0 were
never minified and must stay byte-for-byte as shipped: their hashes are pinned by sites and
registered once by `SdkVersionSeeder`, so re-minifying them would make browsers refuse the script
on every client site. Their `forms.js` is a plain copy, and `ShippedSdkBuildsTests` pins that.
`/sdk/{version}/forms.js` serves the readable source for debugging; never pin it.

### Telephony indicators and the agency on a call (TEL-B-19)

**`tel_call.agency_id` is a COPY, not a join, and that is a correctness decision before it is a
performance one.** It records the agency the call's agent sat in *at the time*, asked of M12 when
the row is opened. A join to the agent's current agency would answer a different question: an agent
transferred in March would move February's figures into their new branch, and February's
conversations with them — so last month's numbers would change after the fact, which is the one
thing a manager reading a report cannot forgive. `TelCall.StampAgency` is therefore **write-once**,
and is retried on every journal reading of a row that still has none, so an M12 blip during a ring
is repaired by the next webhook rather than leaving the call outside every agency for ever.

**`IAdministrationModule.GetUserAgencyAsync(tenantId, userId, ct)` exists beside `GetAgentAsync`
because its callers run outside a request.** The telephony webhook route is deliberately exempt
from host resolution, so it derives its tenant from the connection and carries it by hand;
`GetAgentAsync` leans on the ambient filter and would answer nothing there, or the wrong tenant's
user. It answers `null` for three situations the caller must treat identically — unknown user,
other tenant, genuinely no agency — and never `Guid.Empty`, unlike `AgentSummary.AgencyId`.

**That column is what finally answers "is this call inside my perimeter", via
`TelephonyCallPerimeter`** — the question `tel_call` could not answer, and which two surfaces had
been living without. A supervisor may now correct a mis-filed call of their own branch (TEL-B-09's
call side was "mine, or nobody's" and nothing else), and may listen to a colleague's **unattached**
call of their branch (TEL-B-17 could only ask about the attached record, so an unattached one was
audible to compliance alone). The helper answers **only** the agency question: "my own call" and
"nobody's call" stay with the callers, because the two surfaces mean different things by an
unattributed call. A `null` agency is inside no *restricted* perimeter — never inside all of them —
and a `null` scope from `IAgencyScopeProvider` is UNRESTRICTED, never "no access".

**`tel_call_daily_stat` is recomputed and never incremented.** TEL-B-19 requires the model to be
recalculable, and an incremented counter cannot be: the moment it disagrees with the journal there
is no way back, because resetting and replaying needs a replayable history and the outbox is not an
event store. `TelCallStatsRecomputer` counts a day from `tel_call` and **replaces** the bucket, so a
second run is a no-op rather than a doubling — which is what lets three triggers share one code
path: the wrap-up consumer (the day of the call, so yesterday's missed call qualified this morning
moves yesterday), an hourly job over two days, and a nightly one over seven. The nightly one is not
redundant: what changes a figure *after* the day — a corrected attribution, an anonymisation, an
outcome whose "counts as contact" flag was wrong — is not an event this module sees in time. There
is also `POST telephony/stats/recompute`, because "recalculable" is worth nothing if only a schedule
can exercise it.

**Both grouping columns are non-nullable with `Guid.Empty` for "none".** PostgreSQL treats each
null as distinct, so a nullable agency in `ux_tel_call_daily_stat_bucket` would constrain nothing
and one run would stack a duplicate bucket on the last one's — the same rule the per-person
questionnaires settled. The sentinel does not leave the module: the API edge turns it back into
`null`. A **scoped** caller does not see that bucket at all; showing an unplaceable call inside one
branch's figures would attribute another branch's work to it, and showing it to everybody would have
every manager count the same calls.

**`TelCallDirection.Internal` is written only by a provider, never inferred from the numbers.** The
remote end resolving to no client is the ordinary case for a prospect nobody has captured yet, so
guessing would reclassify — and hide — exactly the calls TEL-B-13 exists to surface. A provider that
does not report internal calls simply never produces the value, and the exclusion has nothing to
exclude: that is the designed fallback, not a gap. `StatDate` is a **UTC** day, stated rather than
hidden — a per-tenant time zone applied there would make yesterday's figures move when somebody
edited a setting.

### What the front end needed and TEL-B-20 had not exposed (TEL-F)

Four endpoints added once the Angular work started and found the surface short. Each one is here
because the alternative was worse than the endpoint.

**`GET telephony/calls` is the journal, and it is PERIMETER-FILTERED — that is the whole point.**
Until it existed the only call lists were "my missed calls" and "the calls of this record", each
scoped by construction. A list over every call of a tenant is a different kind of surface: without
a perimeter it hands any holder of `Telephony.Call.View` the complete telephone activity of every
branch — who spoke to whom, when, for how long, with what outcome. Three things are visible: your
own calls, the ones attributed to **nobody**, and the agencies you cover. The second is deliberate
— it is the "to attribute" queue the missed-call list already shows everyone, because a call from
an unlinked account is the one most likely to be forgotten. A `null` scope from
`IAgencyScopeProvider` is UNRESTRICTED, never "no access".

**The three queues are rules, not filter combinations**, and that is why `TelephonyCallQueue` is an
enum on the backend rather than something the front assembles: `ToQualify` is *ended AND not
wrapped up AND not dismissed*, and the day a fourth condition joins it, a front end that had
rebuilt the rule would be the one that disagreed. `entityType` + `entityId` together give the calls
of one record, which is what a lead's or a client's history asks for.

**`CanListen` is computed, not probed.** Because the list is already perimeter-filtered, the
question reduces to "is there audio and does this caller hold `Telephony.Recording.Listen`" — read
off the token's claims the way `AuthorizationPolicies` renders them. The alternative was a front end
discovering the answer from a 404 per row.

**`GET telephony/calls/export.csv` reuses `ListTelephonyCallsHandler.BuildAsync`**, and that reuse is
what makes it trustworthy: a second copy of the filters and the perimeter would be a second opinion
about who may see what, and the copy that drifts would drift into a file somebody mails outside the
institution. It sits behind `Telephony.Stats.View` rather than `Telephony.Call.View` — reading a
screen of calls and carrying a year of them out of the building are different acts — with the
perimeter still applying on top. UTF-8 **with** a BOM and semicolon-separated, like the mapping
export, because a French Excel opens a comma-separated file as one column and mangles accents
without the BOM. Over 20 000 rows is refused rather than truncated.

**`GET telephony/me` exists because every other read that could answer it is gated behind an
administrator.** `telephony/user-links` needs `Telephony.Settings.Manage` and the connection list
needs `Integration.Connection.Manage`, so the one user who needs to know why their Appeler button is
missing was the one user who could not look. It needs no permission beyond being signed in: the
subject is always `ICurrentUser.Id`, never a parameter. `linkStatus` has four values and not a
boolean because the repairs differ, and a **Broken** link still allows calls — the provider stopped
listing the account, which is a settings row to repair, not a reason to stop an agent mid-shift.
Only `Disabled`, where somebody stated a reason, stops the button.

**`POST telephony/calls/{id}/dismiss` exists because wrapping up was the only way out of the
follow-up list, and it is the wrong instrument.** A wrap-up demands an active outcome code,
publishes an event that becomes a lead activity, and may stop an SLA clock — all of which describes
a *conversation*. "I looked at this and nobody needs calling back" is not one, and forcing it
through a wrap-up would have an institution invent an outcome code for "nothing" and then see it
counted in its contact figures. The **reason is mandatory and lives in the audit row**, not in a
column, for the reason `LinkCallHandler` already states: a column holds the last reason only, while
a reviewer needs the sequence.

**`WrapUpCallRequest.keepRecording` is the only route by which a recording is ever kept under
`OnDemand`** — and there was none, which made the mode selectable and unreachable:
`TelRecordingPolicy.Keeps` reads `TelCall.KeepRecording` and `MarkForRecording` had no caller, so a
compliance officer could choose "à la demande", believe recording was on, and get nothing. The
**policy** decides whether the agent's request means anything, asked at wrap-up time and not
trusted from the request, because the box is rendered from a policy the browser read earlier. It is
*ignored* rather than refused under the other modes — a tab older than a policy change must not
cost an agent their qualification — and a second wrap-up cannot un-ask it, because by then the
fetch job may hold the audio and "forget it" is a retention decision.

**The realtime push now has THREE events, where TEL-B-10 shipped with one.** The original reasoning
— later transitions need no push because "the agent is holding the handset by then" — is true of the
AGENT and false of the SCREEN: the panel has to start a timer when the call is answered and swap
itself for the qualification form when it ends, and with a ring as its only signal it had nothing to
do either on. So `telephony.call.answered` and `telephony.call.ended` join
`telephony.call.ringing`, the four terminal statuses share the one `ended` event, and **the payload
is still a single `callId`** — the argument was about *when* to push, never about what to put in a
frame. Pushes fire on a **change** of status, which `TelCallReading.StatusChanged` is what makes
possible: without it a redelivered webhook, or the catch-up sweep walking twenty pages, would
re-open an agent's panel for a conversation that ended an hour ago. The sweep deliberately pushes
nothing at all — a transition it discovers is up to fifteen minutes old.

**Still missing, and known:** telephony SLA rules, calling hours and the mandatory-qualification
toggle (TEL-F-03's other half) have no endpoint of any kind, so that screen ships with its outcomes
and recording-policy sections only. There is no webhook-URL endpoint and no server-side secret
regeneration (TEL-F-01), and nothing discovers which phone slots a record has filled (TEL-F-04's
number menu is blind to `Primary`/`Secondary`).

### The Ringover adapter is blocked on a test ACCOUNT, not a document (TEL-B-04)

Same shape as `OrassAdapter`, `AmplitudeAdapter`, `SabAdapter` and `PerfectVisionAdapter` —
registered to refuse by name with `IntegrationErrors.AdapterSpecificationPending`, every pure part
implemented and tested, open questions in `RingoverSpecification.OpenQuestions` so procurement and
the class quote the same list — with three differences worth knowing before touching it.

**It cannot be unblocked by reading anything.** The four others wait on a vendor deliverable; this
one waits on a live account, because what has to be right is a vocabulary of statuses and error
bodies that only observed traffic produces. `RingoverStatusTranslation.Known` and
`RingoverErrorClassification.FunctionalCodes` are therefore **empty, with tests that fail the day
somebody adds an entry** — that failure is the prompt to justify the entry from traffic rather than
from a public page.

**Only three of the six open points block anything.** Points 4, 5 and 6 — webhook signing,
click-to-call reference, deletion at the provider — each have a fallback that is already the
production behaviour of lots 2, 3 and 5, recorded in `RingoverFallbacks` with the type that
implements it. Their answers can only replace a working mechanism with a cheaper or more exact one.
The reading "six open points, so six things do not work" is wrong and would have somebody rebuild
what exists.

**One member genuinely works: `ParseWebhook`.** The contract specifies it as pure and tells an
adapter to leave null whatever it cannot determine locally, for the handler to resolve by re-reading
the call — which the journal does unconditionally. The only member with no fallback is the dedup
key, and a SHA-256 of the body is a correct one rather than a placeholder: it needs no field name,
and an identical redelivery produces the same key, which is exactly what `tel_webhook_event` needs.
It deliberately does not look for a call id, a status or a timestamp.

**`RecordingDeletion` is deliberately absent from the capability matrix** while `Recordings` is
present — they are not the same claim. A capability declared on a guess would be *worse* than the
fallback: the retention job would call `DeleteRecordingAsync` on every expiring recording, and a
compliance officer reading the list would believe the provider's copy was being destroyed. Not
declaring it makes the retention's reach exactly what it is, which `RingoverFallbacks.RecordingDeletion`
states in a sentence that can be forwarded to a regulator. That method answers
`CapabilityNotSupported`, never the pending account.

**A guessed status is the worst thing in this repository to get wrong**, and the reason none is
guessed. A wrong endpoint answers 404 and the failure is seen; a wrong status *succeeds* — the
journal takes it, the row changes, nothing reports it. A conversation recorded as missed has
somebody rung to apologise for a call that already happened; a missed call recorded as completed
stops the lead's first-contact clock (TEL-B-12), so the SLA says the branch responded and the
prospect is never called.

**`IntegrationAdapterCompositionTests` declares `[IntegrationKind.Ringover] = true` since lot 7.**
It was `false` with the note that a registration which refused would let an administrator activate a
telephony connection whose every call fails. That is answered by the adapter instead:
`CheckHealthAsync` always answers unhealthy and `ActivateConnectionHandler` requires a passing
check, so the connection still cannot be activated — and the refusal now names the test account
instead of claiming the deployment shipped without the assembly.

### Adding a new module

Use `Sankore.Modules.Administration` as the template: PublicApi project (interface only) + main project (DbContext + domain + Features/) + Tests project. Register in `Program.cs` with `builder.Services.Add{Module}Module(...)` and `appVersion1.Map{Module}ModuleEndpoints()`. Initialize in the startup scope if the module needs migration or seeding.
