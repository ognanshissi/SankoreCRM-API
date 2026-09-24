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

    public static byte[] Generate(LeadSourceConfig source, ServerWebhookSettings settings)
    {
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
                            .Text($"POST /api/ingest/webhook/{source.PublicKey ?? "<YOUR_PUBLIC_KEY>"}")
                            .FontFamily(Fonts.Courier).FontSize(9);
                    });

                    // 2. Expected Format
                    Section(col, "2. Request Format", c =>
                    {
                        c.Item().Text($"Content-Type: {settings.ContentType}").FontSize(10);

                        if (settings.FieldMapping is { Count: > 0 })
                        {
                            c.Item().PaddingTop(8).Text("Field Mapping:").Bold();
                            c.Item().Table(table =>
                            {
                                table.ColumnsDefinition(cols =>
                                {
                                    cols.RelativeColumn(1);
                                    cols.RelativeColumn(1);
                                });

                                table.Header(header =>
                                {
                                    header.Cell().Background(Colors.Blue.Lighten4).Padding(4)
                                        .Text("Your Field").Bold();
                                    header.Cell().Background(Colors.Blue.Lighten4).Padding(4)
                                        .Text("Lead Field").Bold();
                                });

                                foreach (var (externalField, leadField) in settings.FieldMapping)
                                {
                                    table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten3)
                                        .Padding(4).Text(externalField).FontFamily(Fonts.Courier).FontSize(9);
                                    table.Cell().BorderBottom(1).BorderColor(Colors.Grey.Lighten3)
                                        .Padding(4).Text(leadField).FontFamily(Fonts.Courier).FontSize(9);
                                }
                            });
                        }

                        // Example payload
                        c.Item().PaddingTop(8).Text("Example Payload:").Bold();
                        var exampleJson = BuildExamplePayload(settings.FieldMapping);
                        c.Item().Background(Colors.Grey.Lighten4).Padding(8)
                            .Text(exampleJson).FontFamily(Fonts.Courier).FontSize(8);
                    });

                    // 3. Signature
                    if (settings.SignatureAlgorithm is not null)
                    {
                        Section(col, "3. Request Signature", c =>
                        {
                            c.Item().Text("Each request must include an HMAC signature for verification.")
                                .FontSize(10);
                            c.Item().PaddingTop(5).Text($"Algorithm: HMAC-{settings.SignatureAlgorithm.ToUpperInvariant()}")
                                .Bold();
                            c.Item().Text($"Header: {settings.SignatureHeaderName ?? "X-Signature"}")
                                .Bold();
                            c.Item().PaddingTop(5)
                                .Text("Compute HMAC over the raw request body using the shared secret provided during onboarding.")
                                .FontSize(9).Italic();
                            c.Item().PaddingTop(5).Background(Colors.Grey.Lighten4).Padding(8)
                                .Text($"signature = HMAC-{settings.SignatureAlgorithm.ToUpperInvariant()}(body, shared_secret)\n" +
                                      $"{settings.SignatureHeaderName ?? "X-Signature"}: {{signature}}")
                                .FontFamily(Fonts.Courier).FontSize(8);
                            c.Item().PaddingTop(5)
                                .Text("⚠ The secret value is NOT included in this document. It was provided securely during setup.")
                                .FontSize(9).Bold().FontColor(Colors.Red.Darken1);
                        });
                    }

                    // 4. Response Codes
                    Section(col, settings.SignatureAlgorithm is not null ? "4. Response Codes" : "3. Response Codes", c =>
                    {
                        c.Item().Table(table =>
                        {
                            table.ColumnsDefinition(cols =>
                            {
                                cols.ConstantColumn(60);
                                cols.RelativeColumn(1);
                            });

                            ResponseRow(table, "202", "Accepted — lead queued for processing");
                            ResponseRow(table, "400", "Bad Request — malformed JSON or missing required fields");
                            ResponseRow(table, "401", "Unauthorized — invalid or missing public key");
                            ResponseRow(table, "403", "Forbidden — IP not allowed or invalid signature");
                            ResponseRow(table, "409", "Conflict — duplicate external ID (idempotent)");
                            ResponseRow(table, "413", "Payload Too Large — body exceeds 32 KB");
                            ResponseRow(table, "429", "Too Many Requests — rate limit exceeded");
                        });
                    });

                    // 5. Support
                    Section(col, settings.SignatureAlgorithm is not null ? "5. Support" : "4. Support", c =>
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

    private static string BuildExamplePayload(IReadOnlyDictionary<string, string>? fieldMapping)
    {
        if (fieldMapping is null or { Count: 0 })
            return "{\n  \"fullName\": \"Amadou Traoré\",\n  \"phoneNumber\": \"+22370123456\"\n}";

        var lines = fieldMapping.Select(kv =>
        {
            var example = kv.Value.ToLowerInvariant() switch
            {
                "fullname" => "\"Amadou Traoré\"",
                "phonenumber" => "\"+22370123456\"",
                "email" => "\"amadou@example.com\"",
                "firstname" => "\"Amadou\"",
                "lastname" => "\"Traoré\"",
                _ => "\"...\""
            };
            return $"  \"{kv.Key}\": {example}";
        });

        return "{\n" + string.Join(",\n", lines) + "\n}";
    }
}
