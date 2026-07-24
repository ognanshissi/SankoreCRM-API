# SANKORE CRM — .NET Modular Monolith + Vertical Slice Architecture

Reference implementation of Module **M13 — Leads & Dispatching** (feature
`DispatchLead`, F13.9/F13.10) plus a minimal **Users** module, built exactly
as described in the accompanying User Story document. This is a real,
buildable solution — not pseudocode — meant to be opened in Visual
Studio / Rider / VS Code and run locally.

> **Important — how this was produced:** this project was generated in a
> sandboxed environment with **no .NET SDK available** (outbound network
> access is restricted to a small package allow-list that does not include
> Microsoft's package feeds, and the Ubuntu-repo `dotnet-sdk-8.0` package
> has no matching binaries mirrored). Every file here was hand-written to
> be correct, idiomatic, and internally consistent, but **`dotnet build`
> was never actually run against it in this environment.** Please run the
> restore/build steps below in your own environment as the first step, and
> treat the "Known rough edges" section as your review checklist.

## Solution layout

```
SankoreCRM.sln
global.json                          ← pins .NET SDK 9.0.100
Directory.Build.props                ← shared analyzer/nullable settings
src/
  Bootstrapper/
    Sankore.Api/                     ← the ONE host process (ASP.NET Core)
  Shared/
    Sankore.Shared.Kernel/           ← Result, AggregateRoot, GeoPoint... zero deps
    Sankore.Shared.Infrastructure/   ← MediatR behaviors, Outbox, Auth helpers
  Modules/
    Users/
      Sankore.Modules.Users.PublicApi/   ← IUsersModule contract (tiny, no deps)
      Sankore.Modules.Users/             ← internal implementation + EF Core
    Leads/
      Sankore.Modules.Leads.PublicApi/   ← ILeadsModule contract
      Sankore.Modules.Leads/             ← Features/CaptureLead, Features/DispatchLead
      Sankore.Modules.Leads.Tests/       ← xUnit + FluentAssertions + NSubstitute
```

Customers (M01), KYC (M02), and Loans (M04) modules are referenced in
comments in `Program.cs` and `LeadsModule.cs` but intentionally **not**
scaffolded here — the user story only detailed Leads + Users, and adding
three more modules at the same depth would roughly quadruple this
deliverable without teaching anything new: they follow the **identical**
pattern (PublicApi + main assembly + Features/* slices + own DbContext +
own schema). Copy `Sankore.Modules.Users` as a template when you're ready.

## Prerequisites

- .NET SDK 9.0 (see `global.json`) — https://dotnet.microsoft.com/download
- PostgreSQL 16 (Docker is easiest: `docker run -e POSTGRES_PASSWORD=devpassword -p 5432:5432 -d postgres:16`)
- (Optional) RabbitMQ if you want to test `Messaging:UseRabbitMq=true` instead of the default in-memory transport

## Build & test

```bash
dotnet restore
dotnet build
dotnet test src/Modules/Leads/Sankore.Modules.Leads.Tests
```

The test project uses the **EF Core InMemory provider** plus **NSubstitute**
mocks for the cross-module `IUsersModule` dependency — no Docker/Postgres
needed to run these. This trades a bit of PostgreSQL-specific fidelity
(array columns, owned-type SQL generation) for zero external dependencies;
see "Known rough edges" for the recommended follow-up with Testcontainers.

## Database setup

Each module owns its schema and migrates independently:

```bash
# from the repo root
dotnet ef migrations add InitialCreate \
  --project src/Modules/Users/Sankore.Modules.Users \
  --startup-project src/Bootstrapper/Sankore.Api \
  --context UsersDbContext \
  --output-dir Infrastructure/Migrations

dotnet ef migrations add InitialCreate \
  --project src/Modules/Leads/Sankore.Modules.Leads \
  --startup-project src/Bootstrapper/Sankore.Api \
  --context LeadsDbContext \
  --output-dir Infrastructure/Migrations

dotnet ef database update --project src/Modules/Users/Sankore.Modules.Users --startup-project src/Bootstrapper/Sankore.Api --context UsersDbContext
dotnet ef database update --project src/Modules/Leads/Sankore.Modules.Leads --startup-project src/Bootstrapper/Sankore.Api --context LeadsDbContext
```

No migrations are committed to this repo yet — run the commands above once
to generate them for your local Postgres instance.

## Run

```bash
dotnet user-secrets init --project src/Bootstrapper/Sankore.Api
dotnet user-secrets set "Jwt:SigningKey" "some-dev-only-secret-at-least-32-bytes-long" --project src/Bootstrapper/Sankore.Api
dotnet user-secrets set "ConnectionStrings:Postgres" "Host=localhost;Port=5432;Database=sankore_crm_dev;Username=sankore_app;Password=devpassword" --project src/Bootstrapper/Sankore.Api

dotnet run --project src/Bootstrapper/Sankore.Api
```

Swagger UI is available at `/swagger` in Development. `GET /health` is
anonymous; every Leads endpoint requires a valid JWT with a `tenant_id`
claim and the matching `permission` claim (see
`Sankore.Shared.Infrastructure.Auth.AuthorizationPolicies`).

## Where to start reading

1. `src/Modules/Leads/Sankore.Modules.Leads/Features/DispatchLead/` — the
   full vertical slice from the user story: Command → Validator → Handler
   → Endpoint, plus the `CompatibilityScorer` and strategy pattern.
2. `src/Modules/Leads/Sankore.Modules.Leads/Domain/Lead.cs` — the aggregate
   and its invariants.
3. `src/Modules/Leads/Sankore.Modules.Leads.Tests/Features/DispatchLead/` —
   how the slice is tested in isolation.
4. `src/Bootstrapper/Sankore.Api/Program.cs` — how every module is composed
   into one process.

## Known rough edges (read before treating this as production-ready)

Because this was authored without a compiler in the loop, please
specifically double-check the following when you first build it — these
are the places most likely to need a small fix:

- **NuGet package versions.** Version numbers in the `.csproj` files
  (MediatR 12.4.1, MassTransit 8.3.2, EF Core 9.0.0, FluentValidation
  11.10.0, xUnit 2.9.2, NSubstitute 5.1.0, etc.) reflect what was current
  around this project's knowledge cutoff. Run `dotnet restore` and bump
  any that NuGet reports as unresolvable or deprecated.
- **MediatR pipeline behavior registration.** Pipeline behaviors are
  registered globally in `Program.cs` (`AddScoped(typeof(IPipelineBehavior<,>), ...)`)
  rather than per-module. This is correct for MediatR's design, but double
  check the *execution order* matches the intent (Logging → Validation →
  Transaction → Audit) once you can actually step through it — MediatR
  runs behaviors in registration order, outermost first.
- **`TransactionBehavior` uses `System.Transactions.TransactionScope`.**
  This gives cross-DbContext atomicity without a shared DbContext type,
  which is what a modular monolith needs — but it requires the Npgsql
  provider's distributed transaction support to be enabled/compatible with
  your exact Npgsql version. Verify against the Npgsql version resolved by
  `dotnet restore`; the API for enlisting in ambient transactions has
  changed across Npgsql major versions.
- **EF Core InMemory provider limitations in tests.** The InMemory provider
  does not enforce unique constraints, doesn't fully validate `HasQueryFilter`
  the same way relational providers do, and ignores some column-level
  configuration (e.g. `HasColumnType("text[]")` on `AppUser.SpokenLanguages`
  is never exercised because the Leads tests don't touch `UsersDbContext`
  directly — they mock `IUsersModule` instead, which was a deliberate
  choice to keep the module boundary honest in tests too). For true
  PostgreSQL-fidelity tests, promote `DispatchLeadHandlerTests` to a
  Testcontainers-based `Sankore.IntegrationTests` project as outlined in
  the user story (Section 7.2).
- **No migrations are checked in.** See "Database setup" above — generate
  them locally against your own Postgres instance on first run.
- **`ScoredCandidate` is a `public sealed record`** living inside an
  `internal` strategy namespace; C# allows this (the type itself is
  reachable only through internal members) but some analyzers flag public
  types nested under internal-only consumption. Harmless, but you may want
  to mark it `internal` too for clarity — kept `public` here only because
  a couple of analyzer rulesets complain about public interface members
  (`IDispatchingStrategy.EvaluateAsync`) returning internal types.
- **JWT signing key / connection string placeholders** in
  `appsettings.json` are intentionally fake (`CHANGE_ME_...`). Use
  `dotnet user-secrets` (as shown above) or a real secrets manager — never
  put real secrets in `appsettings.json`.
- **RabbitMQ/Kafka are not actually configured with retry/circuit-breaker
  policies** in this scaffold — `Messaging:UseRabbitMq` just points
  MassTransit at a host. Production hardening (prefetch counts, retry
  policies, dead-letter queues) is out of scope for this exercise but
  should be added before go-live per the cahier des charges' security
  section.

## Adding a new slice (worked example from the user story, Section 9)

To add `F13.13 — Refus motivé d'un lead par un agent`:

1. Create `Features/RejectLead/` next to `Features/DispatchLead/`.
2. Add `RejectLeadCommand.cs`, `RejectLeadValidator.cs`, `RejectLeadHandler.cs`,
   `RejectLeadEndpoint.cs` inside it.
3. Add a `Reject(string reason)` method to `Domain/Lead.cs` that calls
   `ReturnToQueue()` internally and raises a new domain event.
4. Add `app.MapRejectLead();` to `LeadsModule.MapLeadsEndpoints()` — the
   **only** line touched outside the new folder.
5. Write `RejectLeadHandlerTests.cs` next to the other tests.

No other slice, no shared service, and no other module's code changes.


# Docker 
To start the project container execute the following code

```console
docker-compose up
```

## Postgres CLI

```console
docker compose exec postgres psql -U sankore_app -d sankore_crm
```

