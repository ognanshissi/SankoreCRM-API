# ORASS®Suite (ORSYS) — drop zone for the artefacts ASS-06 is blocked on

ASS-06's prerequisite is *« spécification d'interface ORASS et accord de l'assureur »*, and its
fourth criterion is *« tests de contrat verts sur l'environnement de test de l'assureur »*.
**Neither the specification nor the agreement exists.** This folder is where the artefacts land; it
is empty because the supplier and partner deliverables have not arrived, and not because nobody has
written the tests.

`OrassSpecificationDropZoneTests` watches this folder. While it is empty, it asserts that the
harness itself is in place. The moment a real document appears here, it **fails on purpose**, with
a message saying what to write next — a failing test is the only notification mechanism in a
repository that cannot be ignored, and the alternative (a test that silently keeps passing next to a
specification nobody mapped) is how a vendor document sits in a repository for a year.

## Two counterparties, not one

Unlike the three blocked core-banking adapters, ASS-06 waits on **two different people**:

- the **specification** may come from ORSYS (the publisher of ORASS®Suite) *or from the insurer*,
  depending on who holds it for this installation;
- the **agreement** can only come from the insurer, and it is not paperwork around the document — it
  is the commercial decision that also settles whether the API is open to this intermediary at all,
  which is the input `OrassCarrierRouting` reads.

A refusal from this adapter therefore names both. Forwarding "we need the ORASS specification" to a
software publisher does not obtain a partner's permission.

## Two carriers, two separate asks

ORASS is **two integrations behind one kind**, the way Amplitude is. Ask for the one the insurer in
front of you actually offers first — `OrassSettings.BaseUrl` is what tells them apart: a connection
with no base URL *is* the statement that no API was opened.

### The API carrier — external API or Bancassurance module

| File to drop here | What it is |
|---|---|
| `api-catalogue.json` / `.yaml` | The operation catalogue, ideally as an OpenAPI document. If ORSYS or the insurer publishes one, the client is **generated** and not hand-written — M02's biometry client is the pattern (`OpenApiReference` + NSwag, regenerated on every build), and the reason is that every field name in a hand-written record is a guess until something checks it. Note M02's scar: NJsonSchema turns `anyOf: [T, null]` into an empty marker class, so a 3.1 document needs the `tools/normalize-openapi-nullable.py` rewrite first. |
| `api-auth.md` | How a call is authenticated, and whether a credential is per intermediary or per branch. |
| `api-idempotency.md` | Whether a replayed call can be made a no-op. Not a detail: every write in this module carries an `IdempotencyKey` because a timeout cannot tell "not subscribed" from "subscribed, answer lost", and an insurer that ignores it turns our retry into a second policy and a second premium on a customer who asked for one. |

### The bordereau carrier — criterion 2's fallback

| File to drop here | What it is |
|---|---|
| `bordereau-layout.md` | Record types, fields, order, fixed-width or delimited, and the code page. Plus: whether ONE bordereau may carry both subscriptions and claims, or whether the insurer demands one per flow — that decides whether a connection's cut-off produces one file or two. `BatchCapableSettings` already offers `CutOffTime`, `FileEncoding` and `FieldSeparator`, so a delimited file in a non-UTF-8 code page is configurable — but not guessable. |
| `outbound.sample` | A real bordereau, exactly as the insurer expects it. |
| `inbound.ack` | A real acknowledgement file. **The artefact whose absence leaves a subscription open forever**: a batch command stays `Batched` until an acknowledgement closes it, and without this format it waits out its `AckTimeoutHours` and alerts. It must also say whether an acknowledgement means *bordereau received* or *policy issued* — different facts, and only the second may ever be shown to a customer as cover in force. |
| `inbound.extraction` | A real portfolio or claim extraction, if the insurer produces one. This is the only thing that could ever put a file-fed insurer's policies on a 360 screen: with no API and no extraction, this module has no policy table and the customer simply shows no cover. |

### Both carriers

| File to drop here | What it is |
|---|---|
| `intermediary-and-branch.md` | How the apporteur code and the branch travel on a request or on a record, and **whether a code is bound to one undertaking** so that the insurer itself refuses a submission naming the other branch. That last half is what decides whether a mis-attributed submission is ever detectable — see `OrassIntermediaryScope`. |
| `test-environment.md` | Coordinates of the insurer's test environment, which carrier it exposes, and confirmation that it carries **both branches**. Asked for **last**: obtained before the contracts, it is an environment with nothing to send it. |

## Four rules about what goes in here

**No file here may be written by us.** A document we draft to "unblock the adapter" tests our own
guess twice: once in the mapper and once in the fixture. That is precisely the failure mode M02's
biometry client demonstrated — hand-written wire records, every field mapping to null, against a
service that answered perfectly. If it did not come from ORSYS or from the insurer, it does not
belong in this folder.

**A single-branch environment does not close criterion 4.** IARD and Vie are separate undertakings
in the CIMA zone, and the assertion that matters is that a submission for one never lands in the
other. One branch would let the branching be wrong and the suite stay green. Ask for both before
accepting the environment.

**No real policyholder data may be committed.** A bordereau sample and an extraction carry insured
names, dates of birth, beneficiaries and sums insured. Anonymise every field before the file lands
in git — this repository is not a place where an insurer's portfolio may end up, and no sample is
worth that.

**No credential, ever.** An API credential, an SFTP password, a sandbox token pasted into a document
in this folder is a credential in git history. Per-connection credentials live in the M12 vault,
under `IntegrationSecrets.CredentialKey` / `SftpCredentialKey` / `SftpHostKeyFingerprintKey`; the
coordinates belong here, the secret never does.

Keep the files exactly as supplied otherwise — in particular, do not re-save a bordereau sample in
UTF-8. The encoding is one of the facts a parser test exists to pin, and a West-African insurer's
back-office export is rarely UTF-8.

## What to do the day something lands here

In this order:

1. **Identify which carrier the artefact describes.** An API catalogue and a bordereau layout
   unblock different halves of this adapter and nothing else.
2. **Generate the client** if there is an OpenAPI document — NSwag through `OpenApiReference`, in
   the adapter project, the way M02's biometry client is generated. If there is no OpenAPI document,
   write one mapping type per operation or per record from the artefact — and nothing the artefact
   does not state.
3. **Put the intermediary code and the branch on every submission first**, before any field mapping
   is written, and make it the transport's or the writer's own invariant rather than a caller's
   responsibility: `OrassIntermediaryScope` already refuses an unattributed connection, and
   `OrassAdapter`'s refusal chain already runs that check before anything else, so whatever is
   written inherits a submission it cannot make unattributed.
4. **Make `CheckHealthAsync` VERIFY the (branch, code) pair** — not merely require it. If a code is
   bound to one undertaking, a probe with the configured pair is the proof; if codes address the
   whole group, an intermediary-listing operation is. Until one of those runs, activation must stay
   closed: that check is the only thing that catches the dangerous case the presence guard cannot, a
   well-formed code belonging to another distributor or to the other branch.
5. **Read the credential per request**, from `ISecretsModule.GetValueAsync` on the key
   `OrassCredential` already probes — never on `DefaultRequestHeaders` of a pooled client, which is
   how one tenant calls with another's token (the mistake `TemenosTransport`'s remarks spell out).
   The SFTP side needs no change: `SftpFileTransport` already reads its own two values at connection
   time, fingerprint included.
6. **On the bordereau carrier, replace the formatter rather than the refusals.**
   `DelimitedOutboundBatchFormatter` is registered with `TryAdd` precisely so an adapter assembly
   that knows its real layout can replace it — and until one does, what the socle would deposit is a
   self-describing projection of what the platform owes, not an ORASS bordereau. Note that the
   dispatcher routes a Batch connection's writes to the socle **before** resolving an adapter, so
   removing this adapter's refusals does nothing for that carrier: the formatter is the whole job
   there.
7. **Remove the refusals one method at a time**, leaving `OrassCapabilityMatrix`,
   `OrassCarrierRouting`, `OrassCredential`, `OrassAdapterRegistration` and the intermediary guard
   untouched. Narrow the matrix as question 2 is answered: an operation the insurer does not expose
   must leave the matrix, not stay in it with a method that fails.
