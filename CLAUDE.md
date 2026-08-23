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

# Run via .NET Aspire (auto-provisions Postgres, RabbitMQ, Seq via Docker)
dotnet run --project SankoreCRM.AppHost
```

### First-time local setup
```bash
dotnet user-secrets init --project src/Bootstrapper/Sankore.Api
dotnet user-secrets set "Jwt:SigningKey" "some-dev-only-secret-at-least-32-bytes-long" --project src/Bootstrapper/Sankore.Api
dotnet user-secrets set "ConnectionStrings:Database" "Host=localhost;Port=5432;Database=sankore_crm_dev;Username=sankore_app;Password=devpassword" --project src/Bootstrapper/Sankore.Api
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

### Administration module specifics

`AdministrationDbContext` extends `IdentityDbContext<AppUser, AppRole, Guid>`. Default schema: `administration`. Migrations history table is in the `identity` schema.

`AdministrationModule.InitializeAsync(sp)` runs migrations + `RoleSeeder` + `PermissionSeeder` at startup. `RoleSeeder` seeds `Roles.All` and grants all permissions to the `System` role.

**AppUser factories:**
- `AppUser.Create(tenantId, agencyId, fullName, email)` — standard user, `AgencyId` required
- `AppUser.CreateRoot(tenantId, fullName, email)` — super-user, no agency, `IsSuperUser = true`, `AccountType = System`
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

### Adding a new module

Use `Sankore.Modules.Administration` as the template: PublicApi project (interface only) + main project (DbContext + domain + Features/) + Tests project. Register in `Program.cs` with `builder.Services.Add{Module}Module(...)` and `appVersion1.Map{Module}ModuleEndpoints()`. Initialize in the startup scope if the module needs migration or seeding.
