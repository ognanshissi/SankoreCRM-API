# Local SMTP secrets setup

The SMTP sender reads credentials from .NET user-secrets (never from `appsettings.json`).
Run the commands below once per machine. They are scoped to the `Sankore.Api` project.

## Quick setup (Mailtrap — recommended for dev)

[Mailtrap](https://mailtrap.io) captures outgoing emails in a sandbox inbox without delivering them.
Create a free account, open **Email Testing → Inboxes → SMTP Settings**, and copy your credentials.

```bash
dotnet user-secrets set "Notifications:Smtp:Host"      "sandbox.smtp.mailtrap.io" --project src/Bootstrapper/Sankore.Api
dotnet user-secrets set "Notifications:Smtp:Port"      "587"                      --project src/Bootstrapper/Sankore.Api
dotnet user-secrets set "Notifications:Smtp:Username"  "<mailtrap-username>"      --project src/Bootstrapper/Sankore.Api
dotnet user-secrets set "Notifications:Smtp:Password"  "<mailtrap-password>"      --project src/Bootstrapper/Sankore.Api
dotnet user-secrets set "Notifications:Smtp:UseSsl"    "false"                    --project src/Bootstrapper/Sankore.Api
dotnet user-secrets set "Notifications:Smtp:FromEmail" "dev@sankore.io"           --project src/Bootstrapper/Sankore.Api
dotnet user-secrets set "Notifications:Smtp:FromName"  "Sankore Dev"              --project src/Bootstrapper/Sankore.Api
```

## Gmail / Google Workspace

Use an [App Password](https://support.google.com/accounts/answer/185833) — not your account password.
2-Step Verification must be enabled on the account first.

```bash
dotnet user-secrets set "Notifications:Smtp:Host"      "smtp.gmail.com"        --project src/Bootstrapper/Sankore.Api
dotnet user-secrets set "Notifications:Smtp:Port"      "587"                   --project src/Bootstrapper/Sankore.Api
dotnet user-secrets set "Notifications:Smtp:Username"  "you@gmail.com"         --project src/Bootstrapper/Sankore.Api
dotnet user-secrets set "Notifications:Smtp:Password"  "<16-char-app-password>" --project src/Bootstrapper/Sankore.Api
dotnet user-secrets set "Notifications:Smtp:UseSsl"    "false"                 --project src/Bootstrapper/Sankore.Api
dotnet user-secrets set "Notifications:Smtp:FromEmail" "you@gmail.com"         --project src/Bootstrapper/Sankore.Api
dotnet user-secrets set "Notifications:Smtp:FromName"  "Sankore"               --project src/Bootstrapper/Sankore.Api
```

## Port reference

| Port | Protocol        | `UseSsl` value |
|------|-----------------|----------------|
| 587  | STARTTLS        | `false`        |
| 465  | Implicit TLS    | `true`         |
| 25   | Plain (no auth) | `false`        |

## Verify your secrets are set

```bash
dotnet user-secrets list --project src/Bootstrapper/Sankore.Api
```

Expected output includes:

```
Notifications:Smtp:Host = ...
Notifications:Smtp:Port = ...
Notifications:Smtp:Username = ...
Notifications:Smtp:Password = ...
Notifications:Smtp:UseSsl = ...
Notifications:Smtp:FromEmail = ...
Notifications:Smtp:FromName = ...
```

## Activating SMTP for a tenant

SMTP is only used when a tenant's notification settings have `ProviderType = "Smtp"` configured
in the Administration module. Tenants without a custom setting fall back to the `Default` provider
(stub logger in dev — no emails are actually sent).

## Notes

- Secrets are stored in `~/.microsoft/usersecrets/<project-id>/secrets.json` on macOS/Linux.
- Never commit `secrets.json` or paste credentials into `appsettings.json` or `appsettings.Development.json`.
- For staging/production, inject secrets via environment variables or a secrets manager
  (e.g. AWS Secrets Manager, Azure Key Vault) — not user-secrets.
