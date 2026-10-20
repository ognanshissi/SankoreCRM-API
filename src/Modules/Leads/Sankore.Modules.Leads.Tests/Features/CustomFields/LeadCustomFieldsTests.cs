namespace Sankore.Modules.Leads.Tests.Features.CustomFields;

using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Administration.PublicApi.Forms;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.CaptureLead;
using Sankore.Modules.Leads.Features.UpdateLead;
using Sankore.Modules.Leads.Tests.Features.CaptureLead;
using Sankore.Modules.Leads.Tests.TestSupport;
using Sankore.Shared.Infrastructure.Messaging;
using Xunit;

/// <summary>
/// The lead form applied to a lead write (dynamic forms, lot 1): custom values checked, stored
/// and merged; the requirements of the form enforced on the interactive capture only.
/// </summary>
public sealed class LeadCustomFieldsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    private readonly Guid _tenant = Guid.NewGuid();
    private readonly TestDbContextFactory _factory;
    private readonly IAdministrationModule _admin = Substitute.For<IAdministrationModule>();

    public LeadCustomFieldsTests() => _factory = new TestDbContextFactory(_tenant);

    public void Dispose() => _factory.Dispose();

    private static readonly FormField Segment = new(
        "segment", FormFieldType.SingleChoice, "Segment", 1, "request",
        QuickAdd: true, IsRequired: true,
        Options: [new FormOption("Commercant", "Commerçant"), new FormOption("Salarie", "Salarié")]);

    private static readonly FormField Sector = new(
        "sector", FormFieldType.Text, "Secteur", 2, "company",
        VisibleWhen: new FormCondition("prospectType", ["Corporate"]));

    private void TenantForm(params FormField[] extra) => TenantForm(f => f, extra);

    private void TenantForm(Func<FormField, FormField> natives, params FormField[] extra)
    {
        var catalog = NativeFormCatalog.For(FormEntity.Lead);
        _admin.GetEntityFormAsync(_tenant, FormEntity.Lead, Arg.Any<CancellationToken>())
            .Returns(new EntityFormDto(FormEntity.Lead, 1, catalog.Sections,
                [.. catalog.Fields.Select(natives), .. extra]));
    }

    private static Dictionary<string, JsonElement> Values(object values)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(values))!;

    private async Task<Sankore.Shared.Kernel.Result<CaptureLeadResult>> CaptureAsync(
        IReadOnlyDictionary<string, JsonElement>? custom, bool interactive = true,
        LeadType type = LeadType.Individual, string? email = null)
    {
        await using var db = _factory.CreateContext();

        var handler = new CaptureLeadHandler(
            db,
            NullLogger<CaptureLeadHandler>.Instance,
            new FixedCaptureClock(Now),
            Substitute.For<MassTransit.IBus>(),
            Substitute.For<Sankore.Modules.Leads.Features.FindDuplicates.IPhoneBlindIndexer>(),
            TestLeadEmailIndexer.Instance,
            Substitute.For<ICustomersModule>(),
            _admin,
            Substitute.For<IEventPublisher>());

        return await handler.Handle(new CaptureLeadCommand(
            TenantId: _tenant,
            FullName: "Awa Ouattara",
            PhoneNumber: "+2250700000001",
            Source: LeadSource.Agency,
            InterestedProduct: "Crédit commerçant",
            PreferredLanguage: "fr",
            Latitude: 0,
            Longitude: 0,
            PreferredAgencyId: null,
            FirstName: "Awa",
            LastName: "Ouattara",
            Email: email,
            ProspectType: type,
            Force: true,
            CustomFields: custom,
            EnforceFormRequirements: interactive), default);
    }

    private async Task<Lead> StoredAsync(Guid id)
    {
        await using var db = _factory.CreateContext();
        return await db.Leads.SingleAsync(l => l.Id == id);
    }

    // ── Capture ──────────────────────────────────────────────────────────

    [Fact]
    public async Task A_valid_custom_value_is_stored_with_the_lead_and_returned_by_its_detail()
    {
        TenantForm(Segment);

        var result = await CaptureAsync(Values(new { segment = "Commercant" }));

        result.IsSuccess.Should().BeTrue(result.Error);
        var lead = await StoredAsync(result.Value.LeadId!.Value);
        Sankore.Modules.Administration.PublicApi.Forms.CustomFieldValues.Read(lead.CustomFieldsJson)!
            ["segment"].GetString().Should().Be("Commercant");
    }

    [Fact]
    public async Task A_value_the_form_refuses_creates_nothing()
    {
        TenantForm(Segment);

        var result = await CaptureAsync(Values(new { segment = "Fonctionnaire" }));

        result.Error.Should().Be("CUSTOM_FIELD_UNKNOWN_OPTION:segment");
        await using var db = _factory.CreateContext();
        (await db.Leads.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_required_custom_field_is_enforced_on_the_screen_and_not_on_an_import()
    {
        TenantForm(Segment);

        (await CaptureAsync(custom: null, interactive: true)).Error.Should().Be("CUSTOM_FIELD_REQUIRED:segment");
        (await CaptureAsync(custom: null, interactive: false)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_native_field_the_tenant_made_required_is_enforced_on_the_server()
    {
        TenantForm(f => f.Key == "email" ? f with { IsRequired = true, QuickAdd = true } : f);

        (await CaptureAsync(custom: null)).Error.Should().Be("FIELD_REQUIRED:email");
        (await CaptureAsync(custom: null, email: "awa@example.ci")).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_company_only_field_is_not_required_of_a_person()
    {
        TenantForm(Sector with { IsRequired = true, QuickAdd = true });

        (await CaptureAsync(custom: null, type: LeadType.Individual)).IsSuccess.Should().BeTrue();
        (await CaptureAsync(custom: null, type: LeadType.Corporate)).Error.Should().Be("CUSTOM_FIELD_REQUIRED:sector");
    }

    [Fact]
    public async Task An_unreadable_form_refuses_custom_values_but_never_loses_a_prospect_without_them()
    {
        _admin.GetEntityFormAsync(_tenant, FormEntity.Lead, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("M12 down"));

        (await CaptureAsync(Values(new { segment = "Commercant" }))).Error.Should().Be("CUSTOM_FIELDS_UNAVAILABLE");
        (await CaptureAsync(custom: null)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task A_capture_carrying_nothing_to_check_never_reads_the_form()
    {
        await CaptureAsync(custom: null, interactive: false);

        await _admin.DidNotReceive().GetEntityFormAsync(
            Arg.Any<Guid>(), Arg.Any<FormEntity>(), Arg.Any<CancellationToken>());
    }

    // ── Update ───────────────────────────────────────────────────────────

    [Fact]
    public async Task An_update_merges_into_the_stored_values_and_may_not_clear_a_required_one()
    {
        TenantForm(Segment, Sector);
        var id = (await CaptureAsync(Values(new { segment = "Commercant" }), type: LeadType.Corporate)).Value.LeadId!.Value;

        var added = await UpdateAsync(id, Values(new { sector = "Commerce de gros" }));
        added.IsSuccess.Should().BeTrue(added.Error);

        var values = Sankore.Modules.Administration.PublicApi.Forms.CustomFieldValues.Read((await StoredAsync(id)).CustomFieldsJson)!;
        values.Keys.Should().BeEquivalentTo("segment", "sector");

        (await UpdateAsync(id, Values(new { segment = (string?)null }))).Error.Should().Be("CUSTOM_FIELD_REQUIRED:segment");
    }

    private async Task<Sankore.Shared.Kernel.Result> UpdateAsync(Guid id, IReadOnlyDictionary<string, JsonElement> custom)
    {
        await using var db = _factory.CreateContext();
        return await new UpdateLeadHandler(db, TestLeadEmailIndexer.Instance, _admin, NullLogger<UpdateLeadHandler>.Instance)
            .Handle(new UpdateLeadCommand(id, null, null, null, null, null, null, null, null, null, null, null, null,
                null, null, null, CustomFields: custom), default);
    }

    // ── The catalogue against this module's own types ────────────────────

    [Fact]
    public void Every_native_lead_key_is_one_the_capture_reports()
    {
        var reported = CaptureLeadHandler.NativeFormValues(new CaptureLeadCommand(
            _tenant, "x", "x", LeadSource.Web, "x", "fr", 0, 0, null)).Keys;

        NativeFormCatalog.For(FormEntity.Lead).Fields.Select(f => f.Key).Should().BeSubsetOf(reported);
    }

    [Theory]
    [InlineData("prospectType", typeof(LeadType))]
    [InlineData("source", typeof(LeadSource))]
    [InlineData("channel", typeof(LeadChannel))]
    public void Native_lead_options_are_the_enum_names(string key, Type enumType)
    {
        Options(key).Should().BeEquivalentTo(Enum.GetNames(enumType));
    }

    [Fact]
    public void Gender_offers_every_value_but_unknown()
    {
        Options("gender").Should().BeEquivalentTo(
            Enum.GetNames<LeadGender>().Where(n => n != nameof(LeadGender.Unknown)));
    }

    private static IEnumerable<string> Options(string key)
        => NativeFormCatalog.For(FormEntity.Lead).Find(key)!.Options!.Select(o => o.Value);
}
