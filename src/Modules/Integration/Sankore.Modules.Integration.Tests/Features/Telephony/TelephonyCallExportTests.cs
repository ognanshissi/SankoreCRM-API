namespace Sankore.Modules.Integration.Tests.Features.Telephony;

using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Sankore.Modules.Integration.Domain;
using Sankore.Modules.Integration.Features.Telephony.Journal;
using Sankore.Modules.Integration.Features.Telephony.Outcomes;
using Sankore.Modules.Integration.Tests.TestSupport;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The journal as a file (TEL-F-08): the bytes Excel needs, and the perimeter the FILE carries.
///
/// <para>
/// The perimeter case is the one that matters here and it is not a duplicate of the list's. A
/// screen showing a row too many is a mistake somebody notices and closes; a file showing one is
/// copied, mailed and kept, and is still readable long after the permission that produced it was
/// revoked. The export reuses <c>BuildAsync</c> precisely so there is one opinion about who may
/// see what — these tests are what says the reuse is real.
/// </para>
/// </summary>
public sealed class TelephonyCallExportTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 3, 11, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The columns the header declares — 19, and every row must carry exactly that many.</summary>
    private const int ColumnCount = 19;

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _connection = Guid.NewGuid();
    private readonly Guid _me = Guid.NewGuid();
    private readonly Guid _colleague = Guid.NewGuid();
    private readonly Guid _myAgency = Guid.NewGuid();
    private readonly Guid _otherAgency = Guid.NewGuid();
    private readonly TestIntegrationDbContextFactory _factory;

    public TelephonyCallExportTests()
    {
        _factory = new TestIntegrationDbContextFactory(_tenant);

        using var db = _factory.CreateContext();
        db.Connections.Add(TelephonyTestHarness.ActiveTelephonyConnection(_tenant, _connection));
        db.SaveChanges();

        TelCallOutcomeSeeder.SeedTenantAsync(db, _tenant, CancellationToken.None)
            .GetAwaiter().GetResult();
        db.SaveChanges();
    }

    public void Dispose() => _factory.Dispose();

    // ── The bytes ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_file_opens_with_a_byte_order_mark_and_the_declared_header()
    {
        await SeedAsync(crmUserId: _me);

        var file = await ExportAsync();

        // The BOM is prepended explicitly rather than left to the host's default encoding: a
        // French Excel without it reads "Rendez-vous décalé" as mojibake, and the person who
        // opens the file blames the data rather than the encoding.
        file.Content.Take(3).Should().Equal(Encoding.UTF8.GetPreamble());

        Lines(file)[0].Should().Be(ExportTelephonyCallsHandler.Header);

        file.ContentType.Should().Be("text/csv; charset=utf-8");

        // Stamped from the injected clock and not DateTimeOffset.UtcNow, so two exports taken a
        // minute apart are two distinct files in a reviewer's download folder.
        file.FileName.Should().Be("appels-20260311-1200.csv");
    }

    [Fact]
    public async Task One_row_per_matching_call_with_the_declared_columns()
    {
        var lead = Guid.NewGuid();

        var callId = await SeedAsync(
            crmUserId: _me, agencyId: _myAgency, outcomeCode: "REACHED",
            match: TelMatchStatus.Matched, entityType: TelEntityTypes.Lead, entityId: lead);

        var file = await ExportAsync();

        var lines = Lines(file);

        lines.Should().HaveCount(2);

        var fields = Fields(lines[1]);

        // Column by column, because a column silently shifting by one is the defect a CSV cannot
        // show: every value still parses, and a reader reconciling an invoice compares ring
        // seconds against talk seconds without noticing.
        fields.Should().HaveCount(ColumnCount);
        fields[0].Should().Be(callId.ToString());
        fields[2].Should().Be("Inbound");
        fields[3].Should().Be("Completed");
        fields[4].Should().Be(_me.ToString());
        fields[5].Should().Be(_myAgency.ToString());
        fields[6].Should().Be(TelEntityTypes.Lead);
        fields[7].Should().Be(lead.ToString());
        fields[8].Should().Be("Matched");
        fields[10].Should().Be("REACHED");
        fields[11].Should().Be("Joint");
        fields[12].Should().Be("True");
        fields[18].Should().Be("false");
    }

    [Fact]
    public async Task An_empty_period_is_a_header_and_nothing_else()
    {
        // A file rather than a refusal: "no calls matched" is an answer, and an error there would
        // have an administrator looking for a fault in a period that was simply quiet.
        var file = await ExportAsync();

        Lines(file).Should().ContainSingle().Which.Should().Be(ExportTelephonyCallsHandler.Header);
    }

    [Fact]
    public async Task The_newest_call_is_the_first_row()
    {
        var old = await SeedAsync(crmUserId: _me, startedAt: Now.AddDays(-3));
        var recent = await SeedAsync(crmUserId: _me, startedAt: Now.AddHours(-1));

        var lines = Lines(await ExportAsync());

        // Same order as the screen, so a reader scrolling the file and a reader scrolling the
        // journal are looking at the same list.
        Fields(lines[1])[0].Should().Be(recent.ToString());
        Fields(lines[2])[0].Should().Be(old.ToString());
    }

    // ── The perimeter, in the file ────────────────────────────────────────────

    [Fact]
    public async Task A_colleagues_OUT_OF_SCOPE_call_is_absent_from_the_FILE()
    {
        var mine = await SeedAsync(crmUserId: _me, agencyId: _myAgency);
        await SeedAsync(crmUserId: _colleague, agencyId: _otherAgency);

        var file = await ExportAsync(scope: [_myAgency]);

        var lines = Lines(file);

        lines.Should().HaveCount(2);
        Fields(lines[1])[0].Should().Be(mine.ToString());

        // Asserted over the WHOLE file and not only over the row count: a column holding the
        // other branch's agency or agent would be a fact about them leaving the building even if
        // the row that carries it looked like one of mine.
        var text = Text(file);

        text.Should().NotContain(_otherAgency.ToString());
        text.Should().NotContain(_colleague.ToString());
    }

    [Fact]
    public async Task An_UNRESTRICTED_caller_exports_the_tenant_whole()
    {
        await SeedAsync(crmUserId: _colleague, agencyId: _otherAgency);
        await SeedAsync(crmUserId: _me, agencyId: _myAgency);

        // Null is unrestricted, never "no access" — and here the wrong reading would hand a
        // compliance officer an empty file that reads as "this institution places no calls".
        Lines(await ExportAsync(scope: null)).Should().HaveCount(3);
    }

    [Fact]
    public async Task Another_tenants_calls_are_absent()
    {
        await SeedAsync(crmUserId: _me, agencyId: _myAgency, tenantId: Guid.NewGuid());

        // Seeded with MY user id and MY agency, so the only thing keeping it out of the file is
        // the global query filter the shared query leans on.
        Lines(await ExportAsync(scope: null)).Should().ContainSingle();
    }

    [Fact]
    public async Task The_filters_are_the_screens_filters()
    {
        await SeedAsync(crmUserId: _me, status: TelCallStatus.Missed);
        await SeedAsync(crmUserId: _me, status: TelCallStatus.Completed);
        await SeedAsync(crmUserId: _me, status: TelCallStatus.Completed, outcomeCode: "REACHED");

        Lines(await ExportAsync(new ListTelephonyCallsQuery(Status: TelCallStatus.Missed)))
            .Should().HaveCount(2);

        // A queue is a rule and not a filter combination, and the export has to honour the same
        // rule — otherwise "export what I am looking at" produces a different list.
        Lines(await ExportAsync(new ListTelephonyCallsQuery(Queue: TelephonyCallQueue.ToQualify)))
            .Should().HaveCount(3);
    }

    [Fact]
    public async Task The_export_is_NOT_paged_like_the_screen()
    {
        for (var i = 0; i < 25; i++)
            await SeedAsync(crmUserId: _me, startedAt: Now.AddMinutes(-i));

        // The point of an export is the whole period. The filters record carries Page and
        // PageSize because it is shared with the screen, and honouring a default PageSize of 20
        // here would hand a reviewer the first 20 rows of a quarter as if it were the quarter.
        Lines(await ExportAsync()).Should().HaveCount(26);
    }

    [Fact]
    public async Task The_file_refuses_EXACTLY_what_the_screen_refuses()
    {
        await SeedAsync(crmUserId: _me);

        // This is the test that says sharing the query actually works. It did not: the type check
        // lived in the list handler while the export called BuildAsync directly, so
        // ?entityType=Invoice was refused on the screen and answered a FILE of the whole
        // perimeter — a widening, on the one surface where a widening is permanent, because a
        // file is copied and kept long after the permission that produced it is revoked. The
        // validation now lives in BuildAsync, where no caller can skip it.
        (await SendAsync(new ListTelephonyCallsQuery(EntityType: "Invoice")))
            .Error.Should().Be(TelephonyErrors.EntityTypeUnknown);

        // And the half pair, for the same reason.
        (await SendAsync(new ListTelephonyCallsQuery(EntityType: TelEntityTypes.Lead)))
            .Error.Should().Be(TelephonyErrors.EntityReferenceIncomplete);
    }

    // ── The row ceiling ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_period_over_the_ceiling_is_refused_and_produces_NO_file()
    {
        for (var i = 0; i < 3; i++) await SeedAsync(crmUserId: _me);

        var result = await SendAsync(maxRows: 2);

        // Refused, not truncated. A truncated export read as complete is worse than no export:
        // somebody reconciles a quarter against its first rows and the figures disagree with the
        // journal for a reason nothing on the page explains.
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(TelephonyErrors.ExportTooLarge);

        // And no bytes came back at all — the guarantee a reviewer actually relies on. Asserted
        // through Result<T> itself, which REFUSES to hand over a value on a failure rather than
        // answering null: there is no way for a caller to half-read a refused export.
        var reachForTheFile = () => result.Value;

        reachForTheFile.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task A_period_EXACTLY_at_the_ceiling_is_allowed()
    {
        for (var i = 0; i < 3; i++) await SeedAsync(crmUserId: _me);

        var result = await SendAsync(maxRows: 3);

        // The comparison is strictly greater-than, so the ceiling is inclusive. Asserted because
        // an off-by-one here refuses the exact export somebody sized their period to fit.
        result.IsSuccess.Should().BeTrue();
        Lines(result.Value).Should().HaveCount(4);
    }

    // ── Escaping, and what no column may carry ────────────────────────────────

    [Fact]
    public async Task An_outcome_label_carrying_a_separator_or_a_quote_keeps_the_column_count()
    {
        const string Awkward = "Rendez-vous; \"ferme\"";

        await using (var db = _factory.CreateContext())
        {
            var outcome = await db.TelCallOutcomes.AsTracking()
                .FirstAsync(o => o.Code == "REACHED", CancellationToken.None);

            // An institution's own wording, which it is free to punctuate. A semicolon in it would
            // otherwise split one row into twenty columns, and every row after it would read with
            // its fields shifted by one — the outcome of one call landing in another's duration.
            outcome.Rename(Awkward);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await SeedAsync(crmUserId: _me, outcomeCode: "REACHED");

        var lines = Lines(await ExportAsync());

        var fields = Fields(lines[1]);

        fields.Should().HaveCount(ColumnCount);
        fields[11].Should().Be(Awkward);

        // Quoted by doubling the quote, which is what every spreadsheet reads back — and the raw
        // line has to show it, or the assertion above would pass against a label whose quotes
        // had simply been stripped.
        lines[1].Should().Contain("\"Rendez-vous; \"\"ferme\"\"\"");
    }

    [Fact]
    public async Task No_column_carries_a_telephone_number_or_the_note_body()
    {
        const string Number = "+22507111111";
        const string Note = "Rappeler le gérant, il est injoignable avant midi.";

        await SeedAsync(
            crmUserId: _me, fromNumber: Number, outcomeCode: "REACHED", note: Note);

        var file = await ExportAsync();

        var text = Text(file);

        // An export is where a number would do the most damage: the file outlives the permission
        // that produced it. The note is the other half — an agent's free text about a person,
        // encrypted in its column, and a decrypted field here would be the one place it leaves
        // the module.
        text.Should().NotContain(Number);
        text.Should().NotContain("22507111111");
        text.Should().NotContain("gérant");
        text.Should().NotContain("Rappeler");

        // And not the ciphertext either: an envelope in a column is still the value, moved.
        text.Should().NotContain("v1:");

        // What a reader DOES get is that there is something to open, inside the application.
        Fields(Lines(file)[1])[18].Should().Be("true");
    }

    [Fact]
    public async Task The_header_names_no_column_that_could_hold_a_number_or_a_note_body()
    {
        var columns = ExportTelephonyCallsHandler.Header.Split(';');

        columns.Should().HaveCount(ColumnCount);

        // Asserted over the HEADER and not over one file's bytes, for the reason the DTO test
        // gives: what has to hold is that there is nowhere to put one. A column added later for
        // convenience would be invisible to a test that only looked at today's rows.
        columns.Should().NotContain(
            c => c.Contains("number", StringComparison.OrdinalIgnoreCase)
                 || c.Contains("phone", StringComparison.OrdinalIgnoreCase)
                 || c.Contains("caller", StringComparison.OrdinalIgnoreCase));

        columns.Where(c => c.Contains("note", StringComparison.OrdinalIgnoreCase))
            .Should().BeEquivalentTo(["hasNote"]);
    }

    // ── Harness ───────────────────────────────────────────────────────────────

    private async Task<Guid> SeedAsync(
        Guid? crmUserId,
        Guid? agencyId = null,
        TelCallStatus status = TelCallStatus.Completed,
        DateTimeOffset? startedAt = null,
        TelMatchStatus match = TelMatchStatus.Unknown,
        string? entityType = null,
        Guid? entityId = null,
        string? outcomeCode = null,
        string? note = null,
        string fromNumber = "+22507111111",
        Guid? tenantId = null)
    {
        var tenant = tenantId ?? _tenant;

        await using var db = _factory.ContextFor(tenant);

        var protector = TelephonyTestHarness.Numbers();
        var from = protector.Protect(fromNumber);
        var to = protector.Protect("+22507000001");
        var at = startedAt ?? Now.AddHours(-1);

        var call = TelCall.Open(
            tenant, _connection, $"prov-{Guid.NewGuid()}",
            TelCallDirection.Inbound, TelCallStatus.Ringing,
            from.Encrypted, from.BlindIndex, to.Encrypted, to.BlindIndex,
            at, externalUserId: null, crmUserId: crmUserId, agencyId: agencyId);

        if (status is TelCallStatus.Completed)
            call.TryMoveTo(TelCallStatus.InProgress, at.AddSeconds(10));

        call.TryMoveTo(status, at.AddSeconds(90));

        if (match != TelMatchStatus.Unknown || entityType is not null)
            call.Attach(match, TelMatchMethod.Number, entityType, entityId)
                .IsSuccess.Should().BeTrue();

        if (outcomeCode is not null || note is not null)
        {
            var noteEnc = note is null
                ? null
                : Encoding.UTF8.GetBytes(TelephonyTestHarness.Encryptor().Encrypt(note)!);

            call.WrapUp(outcomeCode ?? "REACHED", noteEnc, at.AddMinutes(5))
                .IsSuccess.Should().BeTrue();
        }

        db.TelCalls.Add(call);
        await db.SaveChangesAsync(CancellationToken.None);

        return call.Id;
    }

    private async Task<TelephonyCallExport> ExportAsync(
        ListTelephonyCallsQuery? filters = null, Guid[]? scope = null)
    {
        var result = await SendAsync(filters, scope);

        result.IsSuccess.Should().BeTrue($"the export was refused with {result.Error}");

        return result.Value;
    }

    /// <summary>
    /// The export handler over the REAL lister rather than a double, because the one property
    /// worth asserting is that the file and the screen share a query.
    /// </summary>
    /// <param name="maxRows">
    /// The row ceiling for this call. Injected rather than constant so the refusal can be tested
    /// on both sides of the boundary without seeding twenty thousand rows.
    /// </param>
    private async Task<Result<TelephonyCallExport>> SendAsync(
        ListTelephonyCallsQuery? filters = null, Guid[]? scope = null, int? maxRows = null)
    {
        await using var db = _factory.CreateContext();

        var user = TelephonyTestHarness.User(_tenant, _me);

        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([], "TestScheme")),
        };

        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(http);

        var lister = new ListTelephonyCallsHandler(
            db, TelephonyTestHarness.AgencyScope(scope), user, accessor);

        return await new ExportTelephonyCallsHandler(
                lister,
                new TelephonyExportOptions(maxRows ?? ExportTelephonyCallsHandler.DefaultMaxRows),
                user,
                new TelephonyTestHarness.FixedClock(Now),
                NullLogger<ExportTelephonyCallsHandler>.Instance)
            .Handle(
                new ExportTelephonyCallsQuery(filters ?? new ListTelephonyCallsQuery()),
                CancellationToken.None);
    }

    /// <summary>The file as text, BOM stripped.</summary>
    private static string Text(TelephonyCallExport file)
        => Encoding.UTF8.GetString(file.Content, 3, file.Content.Length - 3);

    private static List<string> Lines(TelephonyCallExport file)
        => [.. Text(file).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))];

    /// <summary>
    /// Splits one line the way a spreadsheet does — quote-aware, with a doubled quote standing
    /// for one. A plain <c>Split(';')</c> here would make the escaping test assert nothing: it
    /// would count the separators inside a quoted label as columns and so agree with the bug.
    /// </summary>
    private static List<string> Fields(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (quoted)
            {
                if (c != '"') { current.Append(c); continue; }

                if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; continue; }

                quoted = false;
            }
            else if (c == '"' && current.Length == 0)
            {
                quoted = true;
            }
            else if (c == ';')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());

        return fields;
    }
}
