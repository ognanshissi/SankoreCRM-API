# Perfect Vision sample files — drop zone (INT-28, criterion 4)

INT-28's fourth acceptance criterion is *« les tests de contrat passent sur des fichiers
d'exemple fournis par l'éditeur ou l'IMF »*. **There are no sample files.** This folder is the
harness that will consume them; it is empty because the vendor deliverable has not arrived, and
not because nobody has written the tests.

`PerfectVisionSampleFileTests` watches this folder. While it is empty, it asserts that the harness
itself is in place. The moment a real file appears here, it **fails on purpose**, with a message
saying what to write next — a failing test is the only notification mechanism that cannot be
ignored, and the alternative (a test that silently keeps passing next to a file nobody mapped) is
how a sample file sits in a repository for a year.

## What to drop here, and in what shape

One file per artefact of the exchange, named `<direction>.<artefact>.<n>.<ext>`, byte-for-byte as
the vendor or the IMF supplied it — never retyped, never re-encoded, never "cleaned up". The
encoding is part of what is being tested: a West-African Perfect Vision installation is rarely
UTF-8, and a file converted on the way into git destroys the one fact a parser test needs.

| File | What it must be | Which criterion it unblocks |
|------|-----------------|------------------------------|
| `outbound.customers.1.<ext>` | A customer-creation file **produced by Perfect Vision's own tooling, or validated by the vendor as acceptable input** — the reference our writer must reproduce. | 1 — the writes |
| `outbound.accounts.1.<ext>` | The same for an account opening. | 1 |
| `inbound.ack.1.<ext>` | A real acknowledgement, including **at least one rejected line**. A file of nothing but successes cannot test the path that matters: a batch command that is never closed. | 1 — the command lifecycle |
| `inbound.extraction.1.<ext>` | A balance / position extraction, if the installation produces one. | 2 — the snapshot side |
| `balance-view.columns.txt` | The `\d+ <view>` output, or the vendor's column list for the read-only balance view, with types. | 2 — the live read |

Alongside them, a one-page note answering the three questions in
`PerfectVisionSpecification.OpenQuestions`: **(1)** the file layout and its encoding, **(2)** the
acknowledgement format and how a line identifies the record it answers, **(3)** whether the
read-only balance view exists and its exact columns.

## Two rules about what goes in here

**Real production data must not be committed.** A customer file from an IMF carries names,
identity-document numbers, addresses and dates of birth — the exact fields M01 encrypts at rest.
Ask the vendor for a specimen, or have the IMF pseudonymise before sending. A sample file is worth
nothing if obtaining it created a data-protection incident.

**No file here may be written by us.** A file we invent to "unblock the tests" tests our own guess
twice: once in the writer and once in the fixture, which is precisely the failure mode M02's
biometry client demonstrated — hand-written wire records, every field mapping to null, against a
service that answered perfectly. If it did not come from the vendor or the IMF, it does not belong
in this folder.
