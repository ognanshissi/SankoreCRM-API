using System.Text.RegularExpressions;

namespace Sankore.Modules.Workflow.PublicApi;

/// <summary>
/// Replaces the variables a designer types in a « Notification » step — <c>{{prenom}}</c>,
/// <c>{{nom}}</c>, <c>{{agent}}</c>, <c>{{produit}}</c>, <c>{{institution}}</c> — with the record's
/// values. One renderer for every module that sends one, so a variable means the same thing to a
/// lead, a client and an agent's bell.
///
/// <para>
/// Plain text in, plain text out: the e-mail template escapes it. The text is NOT a Scriban
/// template — a designer's message must not reach the renderer the platform's own templates run in.
/// A variable without a value becomes empty; an unknown one is left as typed, which the editor's
/// preview shows.
/// </para>
/// </summary>
public static partial class WorkflowNotificationText
{
    public static readonly IReadOnlyList<string> Variables = ["prenom", "nom", "agent", "produit", "institution"];

    public static string Render(string? text, WorkflowNotificationValues values)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        return VariablePattern().Replace(text, match => match.Groups[1].Value.ToLowerInvariant() switch
        {
            "prenom"      => values.FirstName ?? string.Empty,
            "nom"         => values.LastName ?? string.Empty,
            "agent"       => values.AgentName ?? string.Empty,
            "produit"     => values.Product ?? string.Empty,
            "institution" => values.Institution ?? string.Empty,
            _             => match.Value,
        }).Trim();
    }

    [GeneratedRegex(@"\{\{\s*([A-Za-z_]+)\s*\}\}")]
    private static partial Regex VariablePattern();
}

/// <param name="LastName">For a company, its name.</param>
public sealed record WorkflowNotificationValues(
    string? FirstName = null,
    string? LastName = null,
    string? AgentName = null,
    string? Product = null,
    string? Institution = null);
