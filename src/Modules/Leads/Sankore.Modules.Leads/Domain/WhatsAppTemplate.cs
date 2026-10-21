namespace Sankore.Modules.Leads.Domain;

using System.Text.RegularExpressions;
using Sankore.Shared.Kernel;

/// <summary>
/// One WhatsApp template of one number, as the catalogue synchronisation last read it, plus what
/// the institution configured on it (WA-BE-04).
///
/// <para>
/// <b>Two owners, two sets of columns.</b> The provider owns the name, the language, the text and
/// above all the approval status — this module never decides that a template may be sent, it
/// reads it. The institution owns <see cref="ProductLineIds"/> (which products the template is
/// offered for) and <see cref="VariableMappings"/> (which lead field fills each <c>{{n}}</c>);
/// a synchronisation never touches those.
/// </para>
///
/// <para>
/// <b>A template the provider stops listing is kept, marked unavailable</b>, rather than deleted:
/// messages already sent point at it, and a template that comes back keeps its mapping.
/// </para>
/// </summary>
public sealed partial class WhatsAppTemplate : ITenant
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid ConnectionId { get; private set; }

    public string Name { get; private set; } = default!;
    public string Language { get; private set; } = default!;

    /// <summary>MARKETING, UTILITY or AUTHENTICATION — Meta bills them differently.</summary>
    public string Category { get; private set; } = default!;

    public WhatsAppTemplateStatus Status { get; private set; }
    public string? ProviderTemplateId { get; private set; }

    public string? HeaderText { get; private set; }
    public string BodyText { get; private set; } = default!;
    public string? FooterText { get; private set; }
    public List<string> Buttons { get; private set; } = [];

    /// <summary>How many <c>{{n}}</c> the body holds — the number of values a send must carry.</summary>
    public int VariableCount { get; private set; }

    /// <summary>The products it is offered for. Empty = every product.</summary>
    public List<Guid> ProductLineIds { get; private set; } = [];

    /// <summary>Which lead field pre-fills each variable. Absent = the agent types it.</summary>
    public List<WhatsAppVariableMapping> VariableMappings { get; private set; } = [];

    /// <summary>False once the provider stopped listing it.</summary>
    public bool IsAvailable { get; private set; }

    public DateTimeOffset SyncedAt { get; private set; }
    public DateTimeOffset? ConfiguredAt { get; private set; }
    public Guid? ConfiguredBy { get; private set; }

    private WhatsAppTemplate() { } // EF Core

    public static WhatsAppTemplate Discover(
        Guid tenantId, Guid connectionId, string name, string language, DateTimeOffset now)
        => new()
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ConnectionId = connectionId,
            Name = name.Trim(),
            Language = language.Trim(),
            Category = "UNKNOWN",
            BodyText = string.Empty,
            SyncedAt = now,
        };

    /// <summary>Whether an agent may be offered it — approved by Meta and still listed.</summary>
    public bool IsSendable => IsAvailable && Status == WhatsAppTemplateStatus.Approved;

    /// <summary>
    /// What the provider says about it now. The institution's columns are left as they are, except
    /// for mappings that name a variable the new text no longer has — those would fill nothing.
    /// </summary>
    public void Refresh(
        string category,
        string status,
        string? providerTemplateId,
        string? header,
        string body,
        string? footer,
        IReadOnlyList<string> buttons,
        DateTimeOffset now)
    {
        Category = string.IsNullOrWhiteSpace(category) ? "UNKNOWN" : category.Trim().ToUpperInvariant();
        Status = ParseStatus(status);
        ProviderTemplateId = providerTemplateId;
        HeaderText = header;
        BodyText = body ?? string.Empty;
        FooterText = footer;
        Buttons = [.. buttons];
        VariableCount = CountVariables(BodyText);
        VariableMappings = [.. VariableMappings.Where(m => m.Index >= 1 && m.Index <= VariableCount)];
        IsAvailable = true;
        SyncedAt = now;
    }

    public void MarkUnavailable(DateTimeOffset now)
    {
        IsAvailable = false;
        SyncedAt = now;
    }

    /// <summary>The institution's part (WA-BE-04 criterion 3).</summary>
    public Result Configure(
        IReadOnlyList<Guid> productLineIds,
        IReadOnlyList<WhatsAppVariableMapping> mappings,
        Guid actor,
        DateTimeOffset now)
    {
        if (mappings.Any(m => m.Index < 1 || m.Index > VariableCount))
            return Result.Fail("WHATSAPP_TEMPLATE_VARIABLE_UNKNOWN");

        if (mappings.Select(m => m.Index).Distinct().Count() != mappings.Count)
            return Result.Fail("WHATSAPP_TEMPLATE_VARIABLE_DUPLICATE");

        ProductLineIds = [.. productLineIds.Where(p => p != Guid.Empty).Distinct()];
        VariableMappings = [.. mappings.OrderBy(m => m.Index)];
        ConfiguredAt = now;
        ConfiguredBy = actor;
        return Result.Ok();
    }

    /// <summary>Whether it is offered for a lead of this product line.</summary>
    public bool AppliesTo(Guid? productLineId)
        => ProductLineIds.Count == 0 || (productLineId is { } p && ProductLineIds.Contains(p));

    /// <summary>The body with its values in place — what the conversation thread shows as sent.</summary>
    public string Render(IReadOnlyList<string> values)
        => VariablePattern().Replace(BodyText, match =>
        {
            var index = int.Parse(match.Groups[1].ValueSpan, System.Globalization.CultureInfo.InvariantCulture);
            return index >= 1 && index <= values.Count ? values[index - 1] : match.Value;
        });

    /// <summary>The highest <c>{{n}}</c> of a body — Meta numbers them from 1 without gaps.</summary>
    public static int CountVariables(string? body)
    {
        if (string.IsNullOrEmpty(body)) return 0;

        var max = 0;
        foreach (Match match in VariablePattern().Matches(body))
        {
            if (int.TryParse(match.Groups[1].ValueSpan, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var n) && n > max)
            {
                max = n;
            }
        }

        return max;
    }

    public static WhatsAppTemplateStatus ParseStatus(string? status) => status?.Trim().ToUpperInvariant() switch
    {
        "APPROVED" => WhatsAppTemplateStatus.Approved,
        "PENDING" or "SUBMITTED" or "IN_APPEAL" or "PENDING_DELETION" => WhatsAppTemplateStatus.Pending,
        "REJECTED" => WhatsAppTemplateStatus.Rejected,
        "PAUSED" => WhatsAppTemplateStatus.Paused,
        "DISABLED" or "DELETED" or "ARCHIVED" => WhatsAppTemplateStatus.Disabled,
        _ => WhatsAppTemplateStatus.Other,
    };

    [GeneratedRegex(@"\{\{\s*(\d{1,2})\s*\}\}")]
    private static partial Regex VariablePattern();
}

/// <summary>Variable <paramref name="Index"/> (<c>{{Index}}</c>) is pre-filled from <paramref name="Field"/>.</summary>
public sealed record WhatsAppVariableMapping(int Index, WhatsAppTemplateField Field);
