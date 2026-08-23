# PostgreSQL — Recovery & Reset Procedures

## Why this happens

`AppHost.cs` uses `.WithDataVolume()` + `.WithLifetime(ContainerLifetime.Persistent)`, which
keeps the Aspire-provisioned Postgres container and its data alive across restarts.

If the schema was ever created outside of EF migrations (old code, `EnsureCreated()`, a SQL
dump), the `__EFMigrationsHistory` table ends up empty while the tables already exist.
`MigrateAsync()` then tries to create those tables again and fails with:

```
42P07: relation "<table>" already exists
```

---

## Rule: never call `EnsureCreated()`

Every module must call `MigrateAsync()` only. `EnsureCreated()` creates the full schema in one
shot without writing to `__EFMigrationsHistory`, permanently breaking future migration runs.

---

## Option A — Full reset (recommended, loses all local data)

Wipes the volume and starts from a clean database. All migrations run from scratch.

```bash
# 1. Find the Aspire Postgres container
docker ps --format "{{.Names}}" | grep postgres

# 2. Find its data volume
docker volume ls | grep sankorecrm

# 3. Stop the container and remove the volume
docker stop <container-name>
docker volume rm <volume-name>

# 4. Restart Aspire — fresh container, all migrations run clean
dotnet run --project SankoreCRM.AppHost
```

Example with the names from this project:

```bash
docker stop postgres-9b4a8ea8
docker volume rm sankorecrm.apphost-9b4a8ea8d6-postgres-data
dotnet run --project SankoreCRM.AppHost
```

---

## Option B — Surgical fix (preserves existing data)

Use when the tables are already correct but the migration history is out of sync.

### 1. Connect to the Aspire Postgres

```bash
# Get the password
docker exec <container> env | grep POSTGRES_PASSWORD

# Open a psql session
docker exec -e PGPASSWORD='<password>' <container> psql -U postgres -d Database
```

### 2. Identify which migrations are already applied

Check for structural evidence of each migration rather than trusting the history table.

```sql
-- Check a column added/dropped by a specific migration
SELECT column_name FROM information_schema.columns
WHERE table_schema = 'administration' AND table_name = 'agencies';

-- Check for a FK or index added by a migration
SELECT conname FROM pg_constraint WHERE conname = 'FK_agencies_agencies_ParentAgencyId';
```

### 3. Record the already-applied migrations

```sql
INSERT INTO administration."__EFMigrationsHistory" ("MigrationId", "ProductVersion") VALUES
  ('20260822202525_Initial',                  '9.0.0'),
  ('20260822233754_UpdateAgency',             '9.0.0'),
  ('20260823101500_AddAgencySelfReferenceFk', '9.0.0');
```

Only insert the migrations whose structural changes are confirmed to be present in the DB.
The next `MigrateAsync()` call will then run only the remaining pending ones.

---

## Option C — Avoid the problem entirely (ephemeral dev database)

Comment out `.WithDataVolume()` in `AppHost.cs` so the Postgres container resets to empty on
every Aspire start. Migrations always run clean; no volume accumulates stale state.

```csharp
// SankoreCRM.AppHost/AppHost.cs
var postgres = builder.AddPostgres("postgres")
    // .WithDataVolume()          ← comment out for ephemeral dev DB
    .WithLifetime(ContainerLifetime.Persistent);
```

Re-enable `WithDataVolume()` when you need data to survive restarts (longer QA cycles, demos).

---

## Migration history table locations

| Schema        | History table path                          |
|---------------|---------------------------------------------|
| administration | `administration."__EFMigrationsHistory"`   |
| leads          | `leads."__EFMigrationsHistory"`            |
| audit          | `audit."__EFMigrationsHistory"`            |
| workflow       | `workflow."__EFMigrationsHistory"`         |

---

## Quick decision guide

| Symptom | Action |
|---------|--------|
| `42P07: relation already exists` | Option A (reset) or Option B (surgical) |
| Missing table on startup | Option A |
| Data you need to keep | Option B |
| Recurring issue across the team | Option C |
