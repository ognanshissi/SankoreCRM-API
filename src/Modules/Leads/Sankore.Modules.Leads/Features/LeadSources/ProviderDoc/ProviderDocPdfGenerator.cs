namespace Sankore.Modules.Leads.Features.LeadSources.ProviderDoc;

using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Sankore.Modules.Leads.Domain;

/// <summary>
/// Generates a personalized PDF documentation for webhook integration.
/// NEVER includes the actual secret — only the algorithm and header name.
/// </summary>
internal static class ProviderDocPdfGenerator
{
    static ProviderDocPdfGenerator()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    /// <param name="signed">
    /// Whether the source has a signing secret — then every push must be signed (WebhookSigning).
    /// Decided by the caller, which can read the vault.
    /// </param>
    public static byte[] Generate(LeadSourceConfig source, ServerWebhookSettings settings, bool signed)
    {
        var header = Ingestion.Webhook.WebhookSigning.HeaderName(settings);
        var algorithm = Ingestion.Webhook.WebhookSigning.Algorithm(settings).ToUpperInvariant();

        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(40);
                page.DefaultTextStyle(x => x.FontSize(10));

                page.Header().Column(col =>
                {
                    col.Item().Text("Webhook Integration Guide")
                        .FontSize(22).Bold().FontColor(Colors.Blue.Darken3);
                    col.Item().Text($"Source: {source.Label} ({source.Code})")
                        .FontSize(12).FontColor(Colors.Grey.Darken1);
                    col.Item().PaddingBottom(10).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
                });

                page.Content().Column(col =>
                {
                    col.Spacing(15);

                    // 1. Endpoint URL
                    Section(col, "1. Endpoint URL", c =>
                    {
                        c.Item().Text("Send lead data via HTTP POST to:")
                            .FontSize(10);
                        c.Item().Background(Colors.Grey.Lighten4).Padding(8)
                            .Text($"POST /api/ingest/hooks/{source.PublicKey ?? "<YOUR_PUBLIC_KEY>"}")
                            .FontFamily(Fonts.Courier).FontSize(9);
                    });

                    // 2. Expected Format
                    Section(col, "2. Request Format", c =>
                    {
                        c.Item().Text($"Content-Type: {settings.ContentType}").FontSize(10);

                        if (settings.FieldMappings is { Count: > 0 })
                        {
                            c.Item().PaddingTop(8).Text("Field Mapping:").Bold();
                            c.Item().Table(table =>
                            {
                                table.ColumnsDefinition(cols =>
                                {
                                    cols.RelativeColumn(1);
                                    cols.RelativeColumn(1);
                                    cols.RelativeColumn(1);
                                });

                                table.Header(header =>
                                {
                                    header.Cell().Background(Colors.Blue.Lighten4).Padding(4)
                                        .Text("Your Field").Bold();
                                    header.Cell().Background(Colors.Blue.Lighten4).Padding(4)
                                        .Text("Lead Field").Bold();
                                    header.Cell().Background(Colors.Blue.Lighten4).Padding(4)
                                        .Text("Transformation").Bold();
                                });

                                foreach (var rule in settings.FieldMappings)
                                {
                                    table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten3)
                                        .Padding(4).Text(rule.SourceField).FontFamily(Fonts.Courier).FontSize(9);
                                    table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten3)
                                        .Padding(4).Text(rule.TargetField).FontFamily(Fonts.Courier).FontSize(9);
                                    table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten3)
                                        .Padding(4).Text(DescribeTransform(rule)).FontFamily(Fonts.Courier).FontSize(9);
                                }
                            });
                        }

                        // Example payload
                        c.Item().PaddingTop(8).Text("Example Payload:").Bold();
                        var exampleJson = BuildExamplePayload(settings.FieldMappings);
                        c.Item().Background(Colors.Grey.Lighten4).Padding(8)
                            .Text(exampleJson).FontFamily(Fonts.Courier).FontSize(8);
                    });

                    // 3. Signature
                    if (signed)
                    {
                        Section(col, "3. Request Signature", c =>
                        {
                            c.Item().Text("Every request MUST be signed. An unsigned or wrongly signed request is refused with 401.")
                                .FontSize(10);
                            c.Item().PaddingTop(5).Text($"Header: {header}").Bold();
                            c.Item().Text($"Algorithm: HMAC-{algorithm}, hexadecimal, lower case").Bold();
                            c.Item().PaddingTop(5)
                                .Text("Sign the timestamp, a dot, then the RAW request body exactly as sent. The timestamp is "
                                      + "Unix seconds and must be within 5 minutes of our clock.")
                                .FontSize(9).Italic();
                            c.Item().PaddingTop(5).Background(Colors.Grey.Lighten4).Padding(8)
                                .Text($"t = current Unix time in seconds\n" +
                                      $"v1 = hex(HMAC-{algorithm}(shared_secret, t + \".\" + raw_body))\n" +
                                      $"{header}: t={{t}},v1={{v1}}")
                                .FontFamily(Fonts.Courier).FontSize(8);
                            c.Item().PaddingTop(5)
                                .Text("When the secret is rotated, the previous one is still accepted for 7 days.")
                                .FontSize(9);
                            c.Item().PaddingTop(5)
                                .Text("⚠ The secret value is NOT included in this document. It was provided securely during setup.")
                                .FontSize(9).Bold().FontColor(Colors.Red.Darken1);
                        });
                    }

                    // 4. Response Codes
                    Section(col, signed ? "4. Response Codes" : "3. Response Codes", c =>
                    {
                        c.Item().Table(table =>
                        {
                            table.ColumnsDefinition(cols =>
                            {
                                cols.ConstantColumn(60);
                                cols.RelativeColumn(1);
                            });

                            ResponseRow(table, "202", "Accepted — one result per item: Accepted, Duplicate, Updated, Rejected or Failed");
                            ResponseRow(table, "400", "Bad Request — malformed JSON, empty payload, or more than 100 items");
                            ResponseRow(table, "401", "Unauthorized — missing, invalid or expired signature");
                            ResponseRow(table, "403", "Forbidden — IP address not allowed");
                            ResponseRow(table, "404", "Not Found — unknown or inactive endpoint URL");
                            ResponseRow(table, "413", "Payload Too Large — body exceeds 256 KB");
                            ResponseRow(table, "429", "Too Many Requests — more than 300 requests a minute");
                        });

                        c.Item().PaddingTop(5).Text(settings.AllowUpdates
                                ? "Sending an item whose external ID was already received UPDATES that lead's custom fields (status \"Updated\")."
                                : "Sending an item whose external ID was already received changes nothing: the first result is returned again.")
                            .FontSize(9);
                    });

                    // 5. Support
                    // Partner events (webhooks lot 6)
                    Section(col, signed ? "5. Reporting events about a lead" : "4. Reporting events about a lead", c =>
                    {
                        c.Item().Text("Tell the CRM that something happened to a lead you sent — an appointment booked, a file "
                                      + "completed. Same signature and IP rules as above.").FontSize(10);
                        c.Item().Background(Colors.Grey.Lighten4).Padding(8)
                            .Text($"POST /api/ingest/hooks/{source.PublicKey ?? "<YOUR_PUBLIC_KEY>"}/events")
                            .FontFamily(Fonts.Courier).FontSize(9);
                        c.Item().PaddingTop(5).Background(Colors.Grey.Lighten4).Padding(8)
                            .Text("{\n" +
                                  "  \"id\": \"evt-123\",                 // your event id: sent twice, counted once\n" +
                                  "  \"type\": \"appointment.booked\",    // lower case, letters, digits . _ -\n" +
                                  "  \"lead\": { \"externalId\": \"…\" },  // or { \"phone\": \"+225…\" }\n" +
                                  "  \"data\": { \"agency\": \"Plateau\", \"amount\": 150000 }  // flat, 30 keys max\n" +
                                  "}")
                            .FontFamily(Fonts.Courier).FontSize(8);
                        c.Item().PaddingTop(5)
                            .Text("Answers 202 with status Matched (the lead was found) or Unmatched (kept, nothing triggered), "
                                  + "200 Duplicate for an id already received, 400 with the reason for a malformed event. "
                                  + "A phone number only matches leads this source sent.")
                            .FontSize(9);
                    });

                    Section(col, signed ? "6. Support" : "5. Support", c =>
                    {
                        c.Item().Text("For integration support, contact your SankoreCRM account manager.")
                            .FontSize(10);
                    });
                });

                page.Footer().AlignCenter()
                    .Text(t =>
                    {
                        t.Span("Generated by SankoreCRM — ").FontSize(8).FontColor(Colors.Grey.Medium);
                        t.Span(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm UTC"))
                            .FontSize(8).FontColor(Colors.Grey.Medium);
                    });
            });
        });

        return doc.GeneratePdf();
    }

    private static void Section(ColumnDescriptor col, string title, Action<ColumnDescriptor> content)
    {
        col.Item().Column(c =>
        {
            c.Item().Text(title).FontSize(14).Bold().FontColor(Colors.Blue.Darken2);
            c.Item().PaddingTop(5);
            content(c);
        });
    }

    private static void ResponseRow(TableDescriptor table, string code, string description)
    {
        table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten3).Padding(4)
            .Text(code).Bold().FontFamily(Fonts.Courier);
        table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten3).Padding(4)
            .Text(description);
    }

    private static string DescribeTransform(FieldMappingRule rule) => rule.Transformation switch
    {
        FieldTransformation.None   => "—",
        FieldTransformation.E164   => $"e164 ({rule.E164Country})",
        FieldTransformation.Concat => $"concat ('{rule.ConcatSeparator ?? " "}')",
        _ => rule.Transformation.ToString().ToLowerInvariant()
    };

    private static string BuildExamplePayload(IReadOnlyList<FieldMappingRule>? fieldMappings)
    {
        if (fieldMappings is null or { Count: 0 })
            return "{\n  \"fullName\": \"Amadou Traoré\",\n  \"phoneNumber\": \"+22370123456\"\n}";

        var lines = fieldMappings.Select(rule =>
        {
            var example = rule.TargetField.ToLowerInvariant() switch
            {
                "fullname" => "\"Amadou Traoré\"",
                "phonenumber" => "\"+22370123456\"",
                "email" => "\"amadou@example.com\"",
                "firstname" => "\"Amadou\"",
                "lastname" => "\"Traoré\"",
                _ => "\"...\""
            };
            return $"  \"{rule.SourceField}\": {example}";
        });

        return "{\n" + string.Join(",\n", lines) + "\n}";
    }
}
