# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Build
dotnet restore
dotnet build

# Run tests (no external dependencies needed — uses EF InMemory + NSubstitute)
dotnet test src/Modules/Leads/Sankore.Modules.Leads.Tests

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

### EF Core migrations (run from repo root, once per module)
```bash
dotnet ef migrations add <Name> \
  --project src/Modules/Leads/Sankore.Modules.Leads \
  --startup-project src/Bootstrapper/Sankore.Api \
  --context LeadsDbContext --output-dir Infrastructure/Migrations

dotnet ef migrations add <Name> \
  --project src/Modules/Users/Sankore.Modules.Users \
  --startup-project src/Bootstrapper/Sankore.Api \
  --context UsersDbContext --output-dir Infrastructure/Migrations

dotnet ef database update --project src/Modules/Leads/Sankore.Modules.Leads \
  --startup-project src/Bootstrapper/Sankore.Api --context LeadsDbContext

dotnet ef database update --project src/Modules/Users/Sankore.Modules.Users \
  --startup-project src/Bootstrapper/Sankore.Api --context UsersDbContext
```

### Docker (without Aspire)
```bash
docker-compose up
docker compose exec postgres psql -U sankore_app -d sankore_crm
```

## Architecture

**Modular Monolith + Vertical Slice** on .NET 10.

```
SankoreCRM.AppHost/          ← Aspire orchestrator (provisions Postgres, RabbitMQ, Seq)
SankoreCRM.ServiceDefaults/  ← Aspire shared defaults (health checks, telemetry)
src/
  Bootstrapper/Sankore.Api/        ← Single host process. Only project that references all modules.
  Shared/Sankore.Shared.Kernel/    ← AggregateRoot, Result<T>, DomainEvent, GeoPoint, ITenantContext. Zero deps.
  Shared/Sankore.Shared.Infrastructure/ ← MediatR behaviors, Outbox, Auth.
  Modules/
    Users/
      Sankore.Modules.Users.PublicApi/  ← IUsersModule contract only (no impl)
      Sankore.Modules.Users/            ← UsersDbContext, AppUser, UsersModuleFacade
    Leads/
      Sankore.Modules.Leads.PublicApi/  ← ILeadsModule contract only
      Sankore.Modules.Leads/            ← Features/CaptureLead, Features/DispatchLead, LeadsDbContext
      Sankore.Modules.Leads.Tests/      ← xUnit + FluentAssertions + NSubstitute
```

### Core rules

**Module isolation:** Modules may only call each other through their `*.PublicApi` interface assembly. No module references another module's domain, infrastructure, or feature internals. Only the Bootstrapper references all module main assemblies.

**Vertical slices:** Each feature lives entirely in `Features/<FeatureName>/` — Command, Validator, Handler, Endpoint. Adding a new slice touches only files inside that folder, plus one `app.Map*();` call in the module's endpoint registration method. No shared services, no cross-slice changes.

**Outbox / IEventPublisher:** Registered as a **keyed service** per module (key = DbContext type name, e.g. `nameof(LeadsDbContext)`) to prevent DI collision between modules. Handlers inject it with `[FromKeyedServices(nameof(LeadsDbContext))] IEventPublisher publisher`. `OutboxProcessor<TDbContext>` is a `IHostedService` that relays rows to MassTransit.

**MediatR pipeline order** (outermost-first, registered in `Program.cs`):
`LoggingBehavior → ValidationBehavior → TransactionBehavior → AuditBehavior → [Handler]`
`TransactionBehavior` uses `System.Transactions.TransactionScope` for cross-DbContext atomicity.

**Multi-tenancy:** `ITenantContext.CurrentTenantId` is resolved from the JWT `tenant_id` claim. Every module's `DbContext` applies EF Core global query filters on all tenant-scoped entities.

**Messaging:** MassTransit, in-memory by default. Set `Messaging:UseRabbitMq=true` in config to switch to RabbitMQ — no module code changes.

**Database:** Each module owns its own PostgreSQL schema (`leads.*`, `users.*`) and migrates independently. `Program.cs` calls `MigrateAsync()` on both DbContexts at startup.

**Authorization:** Policies in `Sankore.Shared.Infrastructure.Auth.AuthorizationPolicies`. JWT claims required: `tenant_id` + `permission` (e.g. `leads:capture`, `leads:dispatch`). Dispatch additionally requires `mfa_verified=true`.

**Swagger UI:** `/swagger` (Development only). Health: `GET /health` (anonymous).

### Adding a new module
Use `Sankore.Modules.Users` as the template: PublicApi project (interface only) + main project (DbContext + domain + Features/) + Tests project. Register in `Program.cs` with `builder.Services.Add{Module}Module(...)` and `app.Map{Module}Endpoints()`.
