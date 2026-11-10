namespace Sankore.Modules.Leads.Features.Opportunities.Quote;

using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Sankore.Modules.Administration.PublicApi;
using Sankore.Modules.Leads.Domain;
using Sankore.Shared.Kernel;

/// <summary>Who the quote is addressed to, as printed.</summary>
/// <param name="Name">The company, else the person.</param>
/// <param name="Attention">« À l'attention de » — the contact, when the name is a company.</param>
/// <param name="Place">City and country, in clear; the street address is never printed (it is encrypted on a lead).</param>
internal sealed record QuoteRecipient(string Name, string? Attention, string? Place, string? Reference);

internal sealed record QuotePdfModel(
    QuoteIssuer Issuer, QuoteLogo? Logo, QuoteRecipient Recipient, string ProjectTitle,
    string Number, DateOnly IssuedOn, DateOnly ValidUntil, string? PaymentTerms,
    ProjectQuote Quote, QuoteTotals Totals, bool TestMode);

/// <summary>
/// The quote PDF (quote lines, lot 5, 2026-11-10, product owner): the institution's letterhead,
/// the recipient, the number, the lines and the totals, the payment terms and the foot — and the
/// mention that it is not an invoice (the invoice stays in the institution's invoicing software,
/// FNE in Côte d'Ivoire). No mandatory mention is invented here: the institution's foot text
/// carries what its accountant asks for (docs/devis-en-lignes.md § 6).
/// </summary>
internal static class ProjectQuotePdf
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private const string Accent = "#1A4D4D";

    static ProjectQuotePdf()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static byte[] Generate(QuotePdfModel m)
    {
        var digits = InstitutionCurrencies.MinorDigits(m.Quote.Currency);
        var unit = m.Quote.Currency is "XOF" or "XAF" ? "FCFA" : m.Quote.Currency;
        string Money(decimal v) => $"{v.ToString(digits == 0 ? "N0" : "N2", Fr)} {unit}";
        string Num(decimal v) => v.ToString("0.###", Fr);
        string Pct(decimal v) => $"{v.ToString("0.##", Fr)} %";
        string Day(DateOnly d) => d.ToString("d MMMM yyyy", Fr);

        var issuer = m.Issuer;
        var lines = m.Quote.Lines.OrderBy(l => l.Position).ToList();

        return Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(36);
            // Ligatures off: with them « Destinataire » copies out of the PDF as « Desnataire » —
            // a quote's text must stay searchable and copyable (found in lot 5's recette).
            page.DefaultTextStyle(x => x.FontSize(9.5f).FontColor(Colors.Grey.Darken4)
                .DisableFontFeature(FontFeatures.StandardLigatures));

            page.Header().Column(col =>
            {
                col.Item().Row(row =>
                {
                    row.RelativeItem().Column(left =>
                    {
                        if (m.Logo is { } logo && TryImage(logo.Content) is { } image)
                            left.Item().Height(48).AlignLeft().Image(image).FitHeight();
                        left.Item().PaddingTop(4).Text(issuer.DisplayName).FontSize(13).Bold().FontColor(Accent);
                        if (!string.IsNullOrWhiteSpace(issuer.LegalForm)) left.Item().Text(issuer.LegalForm);
                        if (!string.IsNullOrWhiteSpace(issuer.HeadOfficeAddress))
                            foreach (var l in issuer.HeadOfficeAddress.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                                left.Item().Text(l);
                        var contact = string.Join(" · ", new[] { issuer.Phone, issuer.Email, issuer.Website }.Where(s => !string.IsNullOrWhiteSpace(s)));
                        if (contact.Length > 0) left.Item().Text(contact).FontColor(Colors.Grey.Darken1);
                    });
                    row.ConstantItem(200).AlignRight().Column(right =>
                    {
                        right.Item().AlignRight().Text("DEVIS").FontSize(22).Bold().FontColor(Accent);
                        right.Item().AlignRight().Text($"N° {m.Number}").Bold();
                        right.Item().AlignRight().Text($"Date : {Day(m.IssuedOn)}");
                        right.Item().AlignRight().Text($"Valable jusqu’au : {Day(m.ValidUntil)}");
                        if (m.TestMode) right.Item().AlignRight().Text("MODE TEST — sans valeur").Bold().FontColor(Colors.Red.Darken2);
                    });
                });
                col.Item().PaddingTop(10).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
            });

            page.Content().PaddingTop(12).Column(col =>
            {
                col.Spacing(12);

                col.Item().Row(row =>
                {
                    row.RelativeItem().Text(t =>
                    {
                        t.Span("Objet : ").SemiBold();
                        t.Span(m.ProjectTitle);
                    });
                    row.ConstantItem(230).Border(1).BorderColor(Colors.Grey.Lighten2).Padding(8).Column(to =>
                    {
                        to.Item().Text("Destinataire").FontSize(8).FontColor(Colors.Grey.Darken1);
                        to.Item().Text(m.Recipient.Name).Bold();
                        if (m.Recipient.Attention is { } who) to.Item().Text($"À l’attention de {who}");
                        if (m.Recipient.Place is { } place) to.Item().Text(place);
                        if (m.Recipient.Reference is { } reference) to.Item().Text(reference).FontColor(Colors.Grey.Darken1);
                    });
                });

                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        // Quantité large enough for « 3 intervention » on one line (lot 6's recette).
                        c.RelativeColumn(3.6f);
                        c.RelativeColumn(1.9f);
                        c.RelativeColumn(1.8f);
                        c.RelativeColumn(1.4f);
                        c.RelativeColumn(1);
                        c.RelativeColumn(1.9f);
                    });
                    table.Header(h =>
                    {
                        foreach (var (label, right) in new[] { ("Désignation", false), ("Quantité", true), ("Prix unitaire HT", true), ("Remise", true), ("TVA", true), ("Total HT", true) })
                        {
                            var cell = h.Cell().Background(Accent).PaddingVertical(5).PaddingHorizontal(4);
                            (right ? cell.AlignRight() : cell).Text(label).FontColor(Colors.White).SemiBold().FontSize(8.5f);
                        }
                    });
                    foreach (var l in lines)
                    {
                        IContainer Cell() => table.Cell().BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(4).PaddingHorizontal(4);
                        Cell().Text(l.Label);
                        Cell().AlignRight().Text(l.Unit is null ? Num(l.Quantity) : $"{Num(l.Quantity)} {l.Unit}");
                        Cell().AlignRight().Text(Money(l.UnitPrice));
                        Cell().AlignRight().Text(l.DiscountKind switch
                        {
                            QuoteDiscountKind.Percent => Pct(l.DiscountValue),
                            QuoteDiscountKind.Amount => Money(l.DiscountAmount),
                            _ => "—",
                        });
                        Cell().AlignRight().Text(l.VatRate == 0 ? "Exonéré" : Pct(l.VatRate));
                        Cell().AlignRight().Text(Money(l.NetExclTax));
                    }
                });

                col.Item().AlignRight().Width(250).Column(tot =>
                {
                    void Line(string label, string value, bool strong = false)
                        => tot.Item().Row(r =>
                        {
                            var l = r.RelativeItem().Text(label);
                            var v = r.ConstantItem(110).AlignRight().Text(value);
                            if (strong) { l.Bold(); v.Bold(); }
                        });
                    if (m.Totals.GlobalDiscountAmount > 0)
                    {
                        Line("Sous-total HT", Money(m.Totals.SubtotalExclTax));
                        Line(m.Quote.GlobalDiscountKind == QuoteDiscountKind.Percent
                            ? $"Remise globale ({Pct(m.Quote.GlobalDiscountValue)})" : "Remise globale", $"− {Money(m.Totals.GlobalDiscountAmount)}");
                    }
                    Line("Total HT", Money(m.Totals.TotalExclTax), strong: true);
                    foreach (var b in m.Totals.VatByRate.Where(b => b.Rate > 0))
                        Line($"TVA {Pct(b.Rate)} sur {Money(b.Base)}", Money(b.Vat));
                    tot.Item().PaddingTop(3).LineHorizontal(1).LineColor(Colors.Grey.Darken1);
                    tot.Item().PaddingTop(3).Row(r =>
                    {
                        r.RelativeItem().Text("Total TTC").FontSize(11).Bold().FontColor(Accent);
                        r.ConstantItem(110).AlignRight().Text(Money(m.Totals.TotalInclTax)).FontSize(11).Bold().FontColor(Accent);
                    });
                });

                col.Item().Column(cond =>
                {
                    cond.Spacing(3);
                    if (!string.IsNullOrWhiteSpace(m.PaymentTerms))
                        cond.Item().Text(t => { t.Span("Conditions de paiement : ").SemiBold(); t.Span(m.PaymentTerms); });
                    cond.Item().Text(t => { t.Span("Validité de l’offre : ").SemiBold(); t.Span($"jusqu’au {Day(m.ValidUntil)}."); });
                });
            });

            page.Footer().Column(foot =>
            {
                foot.Item().LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);
                var legal = string.Join(" · ", new[]
                {
                    issuer.Rccm is { Length: > 0 } r ? $"RCCM {r}" : null,
                    issuer.TaxpayerNumber is { Length: > 0 } n ? $"N° compte contribuable {n}" : null,
                    issuer.TaxRegime is { Length: > 0 } g ? $"Régime : {g}" : null,
                }.Where(s => s is not null));
                if (legal.Length > 0) foot.Item().PaddingTop(4).AlignCenter().Text(legal).FontSize(8).FontColor(Colors.Grey.Darken1);
                if (!string.IsNullOrWhiteSpace(issuer.QuoteFooter))
                    foot.Item().AlignCenter().Text(issuer.QuoteFooter).FontSize(8).FontColor(Colors.Grey.Darken1);
                foot.Item().PaddingTop(2).Row(r =>
                {
                    r.RelativeItem().Text("Devis — ce document n’est pas une facture.").FontSize(8).Italic();
                    r.ConstantItem(80).AlignRight().Text(t =>
                    {
                        t.DefaultTextStyle(s => s.FontSize(8));
                        t.Span("Page ");
                        t.CurrentPageNumber();
                        t.Span(" / ");
                        t.TotalPages();
                    });
                });
            });
        })).GeneratePdf();
    }

    /// <summary>A logo QuestPDF cannot read is left out rather than failing the quote.</summary>
    private static Image? TryImage(byte[] bytes)
    {
        try { return Image.FromBinaryData(bytes); }
        catch (Exception) { return null; }
    }
}
