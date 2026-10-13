namespace Sankore.Shared.Infrastructure.Validation;

using FluentValidation;

/// <summary>
/// Names a rule after the field the CALLER sent, so the 422 can be attached to an input.
///
/// <para>
/// <b>Why this exists.</b> <see cref="Behaviors.ValidationExceptionHandler"/> keys the
/// <c>errors</c> dictionary on <c>ValidationFailure.PropertyName</c>, which FluentValidation
/// fills from the C# expression — and <c>WithName</c> does NOT change it: it sets the DISPLAY
/// name, which only reaches the <c>{PropertyName}</c> placeholder inside the message. A rule
/// written <c>RuleFor(x =&gt; ((ScheduledPullSettings)x.Settings!).EndpointUrl)
/// .WithName("settings.pull.baseUrl")</c> therefore answered
/// <c>{"Settings.EndpointUrl": ["'settings.pull.baseUrl' must not be empty."]}</c>: the message
/// names the editor's field, the key names a <c>[JsonIgnore]</c> projection that appears nowhere
/// in the request. A front-end that attaches messages to inputs by key has nothing to attach to,
/// and the error surfaces as a toast with no field highlighted.
/// </para>
///
/// <para>
/// It is a pair and must stay one — <c>OverridePropertyName</c> for the key, <c>WithName</c> for
/// the message. Overriding the name alone makes FluentValidation derive the display name from the
/// path (<c>"settings.pull.base Url"</c>), which is how the default <c>NotEmpty</c> message reads.
/// </para>
///
/// <para>
/// Use it whenever the validated expression is not the shape the caller posted: a projection, a
/// cast through a polymorphic payload, a nested block, or a whole-object
/// <c>RuleFor(x =&gt; x)</c> cross-field rule — that last one otherwise reports under the empty
/// string.
/// </para>
/// </summary>
public static class ClientFieldNameExtensions
{
    /// <summary>
    /// Reports this rule under <paramref name="jsonPath"/> — the path as it appears in the
    /// request body, camelCase and dotted (<c>"settings.pull.baseUrl"</c>).
    /// </summary>
    public static IRuleBuilderOptions<T, TProperty> WithClientField<T, TProperty>(
        this IRuleBuilderOptions<T, TProperty> rule, string jsonPath)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonPath);

        return rule.OverridePropertyName(jsonPath).WithName(jsonPath);
    }
}
