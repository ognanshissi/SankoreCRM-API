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

**Messaging:** MassTransit in-memory by default. Set `Messaging:UseRabbitMq=true` for RabbitMQ.

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
`Sankore.Modules.Customers.PublicApi.ICustomersModule`. The legacy
`Customer360.PublicApi.ICustomerModule` consumed by Leads is served by
`LegacyCustomerModuleAdapter` — it exposes no sensitive field.

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
on the source the lead was ingested through (`LeadSourceConfig.DefaultDispatchingRuleId`, via
`LeadIngestion` — a `Lead` holds no source-config id), else the highest-priority active rule,
else `DispatchingRule.Default()`. The rule then carries the strategy. A caller that names a
strategy explicitly still gets the rule tuned for it, so the manual dispatch screen is unchanged.
`LeadAssignment.RuleId` records which rule produced an assignment (null = built-in defaults).

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

### Adding a new module

Use `Sankore.Modules.Administration` as the template: PublicApi project (interface only) + main project (DbContext + domain + Features/) + Tests project. Register in `Program.cs` with `builder.Services.Add{Module}Module(...)` and `appVersion1.Map{Module}ModuleEndpoints()`. Initialize in the startup scope if the module needs migration or seeding.
