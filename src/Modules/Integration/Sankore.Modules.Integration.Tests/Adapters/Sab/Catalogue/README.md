# Open SAB catalogue — drop zone (INT-32, criteria 1 and 3)

INT-32's prerequisite is *« catalogue d'API Open SAB, à négocier avec SBS en même temps
qu'Amplitude »*, and its third criterion is *« tests de contrat verts sur l'environnement de test
fourni par l'IMF ou par SBS »*. **Neither exists.** This folder is where the document lands; it is
empty because the supplier deliverable has not arrived, and not because nobody has written the
tests.

`SabCatalogueTests` watches this folder. While it is empty, it asserts that the harness itself is
in place. The moment a real document appears here, it **fails on purpose**, with a message saying
what to write next — a failing test is the only notification mechanism in a repository that cannot
be ignored, and the alternative (a test that silently keeps passing next to a catalogue nobody
mapped) is how a vendor document sits in a repository for a year.

## What to drop here, and in what shape

Byte-for-byte as SBS or the IMF supplied it — never retyped, never "cleaned up", never translated
into our own vocabulary on the way in. A paraphrase of a catalogue is a guess wearing a
document's clothes.

| File | What it must be | Which question it answers |
|------|-----------------|----------------------------|
| `open-sab-openapi.source.json` | The service's own OpenAPI document, if SBS publishes one. **Preferred over every other form**: it makes the client generated rather than hand-written. | 1, 3 |
| `open-sab-catalogue.<ext>` | The catalogue in whatever form SBS actually delivers (PDF, Word, spreadsheet) when there is no OpenAPI document. | 1, 3 |
| `open-sab-auth.<ext>` | How the API key travels — the header or parameter name — **and whether one key is bound to a single entity or addresses the whole installation**. | 2 |
| `open-sab-entities.<ext>` | How `Entity` travels on a request, and whether a service exists that lists the entities a key may address. | 2 |
| `test-environment.<ext>` | The sandbox coordinates, and confirmation that it carries **at least two entities**. | 4 |

## Three rules about what goes in here

**No file here may be written by us.** A document we draft to "unblock the adapter" tests our own
guess twice: once in the mapper and once in the fixture. That is precisely the failure mode M02's
biometry client demonstrated — hand-written wire records, every field mapping to null, against a
service that answered perfectly. If it did not come from SBS or from the IMF, it does not belong
in this folder.

**A single-entity sandbox does not close criterion 3.** The assertion that matters is that a call
scoped to entity A never sees entity B's customers, and one entity would let the scoping be wrong
and the suite stay green. Ask for two before accepting the environment.

**No credential, ever.** An API key, a sandbox password or a bearer token pasted into a document
in this folder is a credential in git history. Per-tenant credentials live in the M12 vault, under
`IntegrationSecrets.CredentialKey(tenantId, connectionId)`; the coordinates belong here, the
secret never does.

## What to do the day something lands here

In this order:

1. **Generate the client** if there is an OpenAPI document — NSwag through `OpenApiReference`, in
   the adapter project, the way M02's biometry client is generated from
   `biometry-openapi.json`. Note M02's scar: NJsonSchema turns `anyOf: [T, null]` into an empty
   marker class, so a 3.1 document needs the `tools/normalize-openapi-nullable.py` rewrite first.
   If there is no OpenAPI document, write one mapping type per operation from the catalogue — and
   nothing that the catalogue does not state.
2. **Put the entity on every request first**, before any field mapping is written, and make it the
   transport's own invariant rather than a caller's responsibility: `SabEntityScope` already
   refuses an unscoped connection, and `SabAdapter`'s refusal chain already runs that check before
   anything else, so the transport inherits a call it cannot make unscoped.
3. **Verify the entity in `CheckHealthAsync`** — not merely its presence. If a key is bound to one
   entity, a probe with the configured entity is the proof; if keys are installation-wide, the
   entity-listing service is. Until one of those runs, activation must stay closed: that check is
   the only thing that catches the dangerous case the presence guard cannot, a well-formed entity
   code belonging to another institution of the same network.
4. **Set the API key per request**, from `ISecretsModule.GetValueAsync` on the key `SabCredential`
   already probes — never on `DefaultRequestHeaders` of a pooled client, which is how one tenant
   calls with another's token (the mistake `TemenosTransport`'s remarks spell out).
5. **Remove the refusals one method at a time**, leaving `SabCapabilityMatrix`,
   `SabAdapterRegistration` and the entity guard untouched. Narrow the matrix as question 3 is
   answered: a service the installation does not expose must leave the matrix, not stay in it with
   a method that fails.
