# SankoreCRM — Backup & Restoration Runbook

**Feature:** F12.8 — Sauvegarde chiffrée et test de restauration
**User Story:** US-M12-BACKUP-001

---

## Architecture overview

```
[postgres container]
      |
      | pg_dump --format=custom
      ↓
[staging dir /tmp/sankore-backup]
      |
      | openssl enc -aes-256-gcm -pbkdf2 -iter 310000
      ↓
[.pgdump.enc + .sha256]
      |
      | aws s3 cp  (or  cp to /mnt/backups)
      ↓
[object storage: s3://sankore-backups/daily/]
```

**Schedule (inside the `backup` container):**

| Job | Cron | Description |
|---|---|---|
| backup.sh | `0 2 * * *` | Daily encrypted dump at 02:00 UTC |
| restore.sh latest | `0 3 * * 0` | Weekly restoration drill, Sunday 03:00 UTC |

---

## Encryption details

| Parameter | Value |
|---|---|
| Algorithm | AES-256-GCM |
| Key derivation | PBKDF2-SHA256, 310 000 iterations |
| Salt | Random, prepended by OpenSSL |
| Key source | `BACKUP_ENCRYPTION_PASSPHRASE` env var (injected at runtime, never stored in image) |

The passphrase is **never** written to disk. It lives in a Docker secret (production) or `.env.backup` (staging only, excluded from git).

---

## First-time setup

### 1. Generate the encryption passphrase

```bash
openssl rand -base64 48
# Store the output in your secrets manager (Vault, AWS Secrets Manager, etc.)
# For Docker Swarm:
printf 'your-generated-passphrase' | docker secret create backup_encryption_passphrase -
printf 'your-db-password'          | docker secret create pg_password -
```

### 2. Configure environment

```bash
cp .env.backup.example .env.backup
# Edit .env.backup — fill in PGPASSWORD, BACKUP_ENCRYPTION_PASSPHRASE, BACKUP_STORAGE_PATH
```

### 3. Build the backup image

```bash
docker compose -f docker-compose.backup.yml build backup
```

### 4. Start the backup scheduler

```bash
docker compose -f docker-compose.backup.yml up -d backup
```

### 5. Verify the first backup immediately

```bash
docker compose -f docker-compose.backup.yml run --rm backup /scripts/backup.sh
```

---

## Manual operations

### Trigger a manual backup

```bash
docker compose -f docker-compose.backup.yml run --rm backup /scripts/backup.sh
```

### List available backups (S3)

```bash
aws s3 ls s3://sankore-backups/daily/ --endpoint-url="${AWS_ENDPOINT_URL}"
```

### List available backups (local)

```bash
ls -lhrt /mnt/backups/*.pgdump.enc
```

### Trigger a manual restoration drill

```bash
# Uses the most recent backup
docker compose -f docker-compose.backup.yml run --rm backup /scripts/restore.sh latest

# Or specify a backup by filename
docker compose -f docker-compose.backup.yml run --rm backup \
    /scripts/restore.sh sankore_crm_20260826_020000.pgdump.enc
```

---

## Production restoration procedure

> **This restores to the LIVE database. Expect downtime. Read every step before starting.**

### Step 1 — Notify

Post in the incident channel. State the target backup timestamp and expected downtime window.

### Step 2 — Stop the API

```bash
docker compose stop sankore-api
```

### Step 3 — Download and decrypt the backup

```bash
BACKUP_FILE=sankore_crm_YYYYMMDD_HHMMSS.pgdump.enc

# Download
aws s3 cp s3://sankore-backups/daily/${BACKUP_FILE} /tmp/${BACKUP_FILE} \
    --endpoint-url "${AWS_ENDPOINT_URL}"
aws s3 cp s3://sankore-backups/daily/${BACKUP_FILE}.sha256 /tmp/${BACKUP_FILE}.sha256 \
    --endpoint-url "${AWS_ENDPOINT_URL}"

# Verify checksum
EXPECTED=$(cat /tmp/${BACKUP_FILE}.sha256)
ACTUAL=$(sha256sum /tmp/${BACKUP_FILE} | awk '{print $1}')
[ "${EXPECTED}" = "${ACTUAL}" ] && echo "OK" || echo "CHECKSUM MISMATCH — abort"

# Decrypt
openssl enc -d -aes-256-gcm -pbkdf2 -iter 310000 \
    -pass env:BACKUP_ENCRYPTION_PASSPHRASE \
    -in  /tmp/${BACKUP_FILE} \
    -out /tmp/restore.pgdump
```

### Step 4 — Restore

```bash
# WARNING: --clean drops all objects before re-creating them.
PGPASSWORD="${PGPASSWORD}" pg_restore \
    --host="${PGHOST}" \
    --port="${PGPORT}" \
    --username="${PGUSER}" \
    --dbname="${PGDATABASE}" \
    --clean \
    --if-exists \
    --no-owner \
    --no-acl \
    --exit-on-error \
    /tmp/restore.pgdump
```

### Step 5 — Smoke test

```bash
PGPASSWORD="${PGPASSWORD}" psql \
    --host="${PGHOST}" --port="${PGPORT}" \
    --username="${PGUSER}" --dbname="${PGDATABASE}" \
    --file=scripts/backup/smoke-test.sql
```

All counts must be > 0. If any are 0, do **not** restart the API — escalate.

### Step 6 — Restart the API and verify

```bash
docker compose start sankore-api
curl -f https://api.sankore.sn/health
```

### Step 7 — Close the incident

Update the incident log with: backup used, restoration start/end time, smoke-test results, operator.

---

## Retention policy

Retention is enforced by an S3 lifecycle rule on the backup bucket — **not** by the scripts.

| Tier | Count | Rule |
|---|---|---|
| Daily | 30 | Delete objects with prefix `daily/` older than 30 days |
| Weekly | 12 | Copy Sunday backups to `weekly/` prefix; delete after 84 days |

Configure these rules once in your S3 console / Terraform. The scripts do not manage S3 retention.

For local filesystem storage (`/mnt/backups`), `backup.sh` prunes `.pgdump.enc` files older than `BACKUP_RETENTION_DAYS` (default: 30) automatically.

---

## Encryption key rotation

Key rotation requires re-encrypting all existing backups with the new key. Do this during a low-traffic window.

```bash
# 1. Set both old and new passphrases in your shell
OLD_PASSPHRASE="..."
NEW_PASSPHRASE="..."

# 2. For each backup file in storage:
for ENC_FILE in *.pgdump.enc; do
    # Decrypt with old key
    openssl enc -d -aes-256-gcm -pbkdf2 -iter 310000 \
        -pass pass:"${OLD_PASSPHRASE}" -in "${ENC_FILE}" -out tmp.pgdump

    # Re-encrypt with new key
    openssl enc -aes-256-gcm -pbkdf2 -iter 310000 \
        -pass pass:"${NEW_PASSPHRASE}" -in tmp.pgdump -out "${ENC_FILE}.new"

    # Replace and update checksum
    mv "${ENC_FILE}.new" "${ENC_FILE}"
    sha256sum "${ENC_FILE}" | awk '{print $1}' > "${ENC_FILE}.sha256"
    rm -f tmp.pgdump
done

# 3. Update the secret in your secrets manager
# 4. Redeploy the backup container with the new BACKUP_ENCRYPTION_PASSPHRASE
```

---

## Alarm conditions

| Condition | Action |
|---|---|
| `backup.sh` exits non-zero | Page on-call. Check container logs: `docker logs sankore-backup` |
| `restore.sh` exits 2 (smoke test failed) | Page on-call. Run a manual drill against an older backup to isolate the failure |
| No backup file created in > 25 hours | S3 age-based CloudWatch alarm (or equivalent) should fire |
| Checksum mismatch on download | Do **not** proceed with restoration. Escalate — may indicate storage tampering |

---

## Viewing backup logs

```bash
# Live log stream
docker logs -f sankore-backup

# Last 100 lines
docker logs --tail 100 sankore-backup
```

Logs are also forwarded to Seq via the container's stdout log driver.

---

## What is NOT covered

- **Point-in-time recovery (PITR)** — this runbook uses daily logical dumps (`pg_dump`). PITR via WAL archiving (`pgBackRest`, `pg_basebackup`) can be added as a follow-up if the RPO target requires it.
- **Cross-region backup replication** — configure S3 cross-region replication rules in your infra layer.
- **Backup of Redis** — Redis is used as a notification-provider cache only; it holds no durable state and does not require backup.
