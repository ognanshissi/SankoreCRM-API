# Amplitude (SBS) — drop zone for the artefacts INT-31 is blocked on

INT-31's criterion 4 — « tests de contrat verts sur l'environnement de test fourni par l'IMF ou par
SBS » — **cannot be met**: there is no interface contract, no API access and no test environment.
What is deliverable without SBS is the harness that will consume the artefacts, and a tripwire that
fires the day one of them lands here (`AmplitudeSbsArtifactTests`).

Amplitude is **two integrations behind one kind**, so there are **two separate asks of SBS**. An
answer to one unblocks nothing about the other. Ask for the one the installation in front of you
runs first — `AmplitudeSettings.AmplitudeVersion` is what tells them apart.

## What to ask SBS for

### Amplitude Up (`AmplitudeVersion.Up`) — the API path

| File to drop here | What it is |
|---|---|
| `api-catalogue.json` / `.yaml` | The service catalogue, ideally as an OpenAPI document. If SBS publishes one, the client is **generated** and not hand-written — M02's biometry client is the pattern (`OpenApiReference` + NSwag, regenerated on every build), and the reason is that every field name in a hand-written record is a guess until something checks it. |
| `api-auth.md` | How a call is authenticated, and whether a token is per tenant. |
| `api-idempotency.md` | Whether a replayed call can be made a no-op. Not a detail: every write in this module carries an `IdempotencyKey` because a timeout cannot tell "not created" from "created, answer lost", and a service that ignores it turns our retry into a second customer. |

### Pre-Up (`AmplitudeVersion.Legacy`) — the batch path

| File to drop here | What it is |
|---|---|
| `file-layout.md` | Record types, fields, order, fixed-width or delimited, and the code page. `BatchCapableSettings` already offers `FileEncoding` and `FieldSeparator`, so a delimited file in a non-UTF-8 code page is configurable — but not guessable. |
| `outbound.sample` | A real outbound file, exactly as Amplitude expects it. |
| `inbound.ack` | A real acknowledgement file. **The artefact whose absence leaves a command open forever**: a batch command stays `Batched` until an acknowledgement closes it, and without this format it waits out its `AckTimeoutHours` and alerts. |
| `inbound.extraction` | A real daily extraction, if the installation produces one. |

### Both

| File to drop here | What it is |
|---|---|
| `test-environment.md` | Coordinates of the institution's or SBS's test environment, and whether one set of credentials covers both carriers. Asked for **last**: obtained before the contracts, it is an environment with nothing to send it. |

## Rules for this folder

- **Real customer data must not be committed.** An outbound sample and an extraction carry names,
  identity-document numbers and balances. Anonymise every field before the file lands in git — this
  repository is not a place where an institution's portfolio may end up, and no sample is worth
  that.
- Credentials and tokens are not files: they belong in the M12 secrets vault
  (`AmplitudeSettings.CredentialVaultRef`), never here and never in a settings column.
- Keep the files exactly as supplied otherwise — in particular, do not re-save a batch sample in
  UTF-8. The encoding is one of the facts a parser test exists to pin, and a West-African CBS
  deployment is rarely UTF-8.
