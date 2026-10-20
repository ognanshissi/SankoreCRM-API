namespace Sankore.Modules.Administration.Tests.Domain.Forms;

using FluentAssertions;
using Sankore.Modules.Administration.Domain.Forms;
using Sankore.Modules.Administration.PublicApi.Forms;
using Xunit;

/// <summary>
/// What a tenant may save as a form layout, and how the saved layout merges with the native
/// catalogue on read.
/// </summary>
public sealed class EntityFormLayoutTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Admin = Guid.NewGuid();

    /// <summary>The default lead form, as the editor would post it back untouched.</summary>
    private static FormSectionsAndFields DefaultLead()
    {
        var form = EntityFormResolver.Resolve(FormEntity.Lead, 0, null);
        return new FormSectionsAndFields(form.Sections, form.Fields);
    }

    private static FormSectionsAndFields With(FormSectionsAndFields layout, params FormField[] extra)
        => layout with { Fields = [.. layout.Fields, .. extra] };

    private static FormSectionsAndFields Replace(FormSectionsAndFields layout, string key, Func<FormField, FormField> change)
        => layout with { Fields = layout.Fields.Select(f => f.Key == key ? change(f) : f).ToList() };

    private static FormField Custom(string key, FormFieldType type = FormFieldType.Text, string section = "request")
        => new(key, type, "Libellé " + key, 99, section);

    private static EntityFormLayout Saved(FormSectionsAndFields layout)
    {
        var entity = EntityFormLayout.Create(Tenant, FormEntity.Lead);
        var result = entity.Replace(layout, 0, Admin, DateTimeOffset.UtcNow);
        result.IsSuccess.Should().BeTrue(result.Error);
        return entity;
    }

    private static string? SaveError(FormSectionsAndFields layout, EntityFormLayout? existing = null)
    {
        var entity = existing ?? EntityFormLayout.Create(Tenant, FormEntity.Lead);
        return entity.Replace(layout, entity.Version, Admin, DateTimeOffset.UtcNow).Error;
    }

    // ── Read ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(FormEntity.Lead)]
    [InlineData(FormEntity.Client)]
    [InlineData(FormEntity.Product)]
    public void A_tenant_that_never_saved_gets_the_native_form_at_version_zero(FormEntity entity)
    {
        var form = EntityFormResolver.Resolve(entity, 0, null);

        form.Version.Should().Be(0);
        form.Fields.Should().NotBeEmpty().And.OnlyContain(f => f.IsNative);
        form.Fields.Where(f => f.IsRequiredLocked).Should().OnlyContain(f => f.IsRequired && f.QuickAdd);
        form.Fields.Select(f => f.SectionKey).Should().OnlyContain(k => form.Sections.Any(s => s.Key == k));
    }

    [Fact]
    public void The_default_layout_saves_as_it_is()
    {
        Saved(DefaultLead()).Version.Should().Be(1);
    }

    [Fact]
    public void A_native_field_keeps_its_type_and_options_whatever_the_editor_sends()
    {
        var tampered = Replace(DefaultLead(), "gender", f => f with
        {
            Type = FormFieldType.Text,
            Options = [new FormOption("X", "X")],
            Label = "Sexe",
        });

        var form = EntityFormResolver.Resolve(FormEntity.Lead, 1, Saved(tampered).Layout);
        var gender = form.Fields.Single(f => f.Key == "gender");

        gender.Type.Should().Be(FormFieldType.SingleChoice);
        gender.Options!.Select(o => o.Value).Should().Equal("Male", "Female", "Other");
        gender.Label.Should().Be("Sexe");
    }

    [Fact]
    public void A_native_field_the_saved_layout_does_not_mention_is_appended_last()
    {
        var withoutComment = DefaultLead() with
        {
            Fields = DefaultLead().Fields.Where(f => f.Key != "comment").ToList(),
        };

        var form = EntityFormResolver.Resolve(FormEntity.Lead, 1, Saved(withoutComment).Layout);

        form.Fields.Last().Key.Should().Be("comment");
    }

    [Fact]
    public void A_custom_field_is_returned_with_the_natives()
    {
        var form = EntityFormResolver.Resolve(FormEntity.Lead, 1,
            Saved(With(DefaultLead(), Custom("segment"))).Layout);

        form.Fields.Should().ContainSingle(f => f.Key == "segment" && !f.IsNative);
    }

    // ── Write: the flags ─────────────────────────────────────────────────

    [Theory]
    [InlineData(false, true, false)]   // made optional
    [InlineData(true, false, false)]   // taken off the creation form
    [InlineData(true, true, true)]     // hidden
    public void A_field_the_entity_requires_cannot_be_relaxed(bool required, bool quickAdd, bool hidden)
    {
        var layout = Replace(DefaultLead(), "phoneNumber",
            f => f with { IsRequired = required, QuickAdd = quickAdd, IsHidden = hidden });

        SaveError(layout).Should().EndWith(":phoneNumber");
    }

    [Fact]
    public void A_required_field_must_be_on_the_creation_form()
    {
        var layout = Replace(DefaultLead(), "email", f => f with { IsRequired = true, QuickAdd = false });

        SaveError(layout).Should().Be("FORM_REQUIRED_FIELD_NOT_IN_QUICK_ADD:email");
    }

    [Fact]
    public void An_optional_native_field_can_be_made_required_or_hidden()
    {
        var layout = Replace(Replace(DefaultLead(), "email", f => f with { IsRequired = true, QuickAdd = true }),
            "website", f => f with { IsHidden = true });

        var form = EntityFormResolver.Resolve(FormEntity.Lead, 1, Saved(layout).Layout);

        form.Fields.Single(f => f.Key == "email").IsRequired.Should().BeTrue();
        form.Fields.Single(f => f.Key == "website").IsHidden.Should().BeTrue();
    }

    [Fact]
    public void A_field_and_a_section_each_keep_their_own_quick_add_flag()
    {
        var layout = DefaultLead() with
        {
            Sections = DefaultLead().Sections.Select(s => s.Key == "origin" ? s with { QuickAdd = false } : s).ToList(),
        };
        layout = Replace(layout, "campaign", f => f with { QuickAdd = true });

        var form = EntityFormResolver.Resolve(FormEntity.Lead, 1, Saved(layout).Layout);

        form.Sections.Single(s => s.Key == "origin").QuickAdd.Should().BeFalse();
        form.Fields.Single(f => f.Key == "campaign").QuickAdd.Should().BeTrue();
    }

    // ── Write: custom fields ─────────────────────────────────────────────

    [Theory]
    [InlineData("Segment")]
    [InlineData("address.city2")]
    [InlineData("a")]
    [InlineData("mon-champ")]
    public void A_custom_key_must_be_camel_case_letters_and_digits(string key)
    {
        SaveError(With(DefaultLead(), Custom(key))).Should().Be($"FORM_FIELD_KEY_INVALID:{key}");
    }

    [Fact]
    public void A_choice_needs_options_and_a_reference_is_native_only()
    {
        SaveError(With(DefaultLead(), Custom("segment", FormFieldType.SingleChoice)))
            .Should().Be("FORM_FIELD_OPTIONS_REQUIRED:segment");
        SaveError(With(DefaultLead(), Custom("agence", FormFieldType.Reference)))
            .Should().Be("FORM_FIELD_TYPE_NOT_SUPPORTED:agence");
    }

    [Fact]
    public void A_limit_that_does_not_apply_to_the_type_is_refused_rather_than_stored_and_ignored()
    {
        SaveError(With(DefaultLead(), Custom("since", FormFieldType.Date) with { Min = 0 }))
            .Should().Be("FORM_FIELD_RANGE_NOT_APPLICABLE:since");
        SaveError(With(DefaultLead(), Custom("note") with { Pattern = "([" }))
            .Should().Be("FORM_FIELD_PATTERN_INVALID:note");
    }

    [Fact]
    public void A_default_the_field_itself_would_refuse_is_refused()
    {
        var field = Custom("segment", FormFieldType.SingleChoice) with
        {
            Options = [new FormOption("A", "A")],
            DefaultValue = "Z",
        };

        SaveError(With(DefaultLead(), field)).Should().Be("FORM_FIELD_DEFAULT_INVALID:segment");
    }

    [Fact]
    public void A_saved_custom_field_is_archived_never_removed_and_never_changes_type()
    {
        var saved = Saved(With(DefaultLead(), Custom("segment")));

        SaveError(DefaultLead(), saved).Should().Be("FORM_FIELD_CANNOT_BE_REMOVED:segment");
        SaveError(With(DefaultLead(), Custom("segment", FormFieldType.Integer)), saved)
            .Should().Be("FORM_FIELD_TYPE_IMMUTABLE:segment");
        SaveError(With(DefaultLead(), Custom("segment") with { IsArchived = true }), saved)
            .Should().BeNull();
    }

    // ── Write: conditions and versions ───────────────────────────────────

    [Fact]
    public void A_condition_may_name_a_native_choice_with_one_of_its_values()
    {
        var field = Custom("sector") with { VisibleWhen = new FormCondition("prospectType", ["Corporate"]) };

        SaveError(With(DefaultLead(), field)).Should().BeNull();
    }

    [Theory]
    [InlineData("firstName", "x")]      // not a choice
    [InlineData("prospectType", "Ngo")] // not one of its options
    [InlineData("nowhere", "x")]        // no such field
    [InlineData("sector", "x")]         // itself
    public void A_condition_that_could_never_be_evaluated_is_refused(string target, string value)
    {
        var field = Custom("sector") with { VisibleWhen = new FormCondition(target, [value]) };

        SaveError(With(DefaultLead(), field)).Should().Be("FORM_CONDITION_INVALID:sector");
    }

    [Fact]
    public void A_save_against_a_version_already_replaced_is_refused()
    {
        var saved = Saved(DefaultLead());

        saved.Replace(DefaultLead(), expectedVersion: 0, Admin, DateTimeOffset.UtcNow)
            .Error.Should().Be("FORM_VERSION_CONFLICT");
    }

    [Fact]
    public void Orders_are_renumbered_within_each_section()
    {
        var layout = With(DefaultLead(), Custom("alpha") with { Order = 50 }, Custom("beta") with { Order = 7 });

        var saved = Saved(layout).Layout.Fields.Where(f => f.SectionKey == "request").ToList();

        saved.Select(f => f.Order).Should().Equal(Enumerable.Range(1, saved.Count));
        saved.Select(f => f.Key).TakeLast(2).Should().Equal("beta", "alpha");
    }
}
