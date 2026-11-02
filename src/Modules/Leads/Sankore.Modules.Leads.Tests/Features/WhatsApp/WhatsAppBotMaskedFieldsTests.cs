namespace Sankore.Modules.Leads.Tests.Features.WhatsApp;

using FluentAssertions;
using Sankore.Modules.Administration.PublicApi.Forms;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Features.WhatsApp.Bot;
using Xunit;

/// <summary>
/// A native field the institution masked in the form settings is not asked for by the bot either
/// (product owner, 2026-11-02) — « Montant souhaité » is masked by default.
/// </summary>
public sealed class WhatsAppBotMaskedFieldsTests
{
    private static EntityFormDto Form(bool amountHidden)
    {
        var catalog = NativeFormCatalog.For(FormEntity.Lead);
        var fields = catalog.Fields
            .Select(f => f.Key == "desiredAmount" ? f with { IsHidden = amountHidden } : f)
            .ToList();
        return new EntityFormDto(FormEntity.Lead, 1, catalog.Sections, fields);
    }

    [Fact]
    public void A_masked_native_field_is_neither_listed_nor_offered()
    {
        var form = Form(amountHidden: true);

        GetWhatsAppBotOverviewHandler.Fields(form).Select(f => f.Code)
            .Should().NotContain(WhatsAppBotFields.DesiredAmount)
            .And.Contain(WhatsAppBotFields.Profession);

        var ctx = new BotFieldContext([], form);
        ctx.IsOffered(WhatsAppBotFields.DesiredAmount).Should().BeFalse();
        ctx.IsOffered(WhatsAppBotFields.Neighbourhood).Should().BeTrue("the quartier is not on the form, so nothing masks it");
    }

    [Fact]
    public void A_visible_field_is_offered_and_an_unreadable_form_masks_nothing()
    {
        new BotFieldContext([], Form(amountHidden: false)).IsOffered(WhatsAppBotFields.DesiredAmount).Should().BeTrue();
        new BotFieldContext([], null).IsOffered(WhatsAppBotFields.DesiredAmount).Should().BeTrue();
    }
}
