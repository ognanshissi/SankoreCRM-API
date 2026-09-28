namespace Sankore.Modules.Customers.Resources;

/// <summary>
/// Marker class for Customers (M01) module error strings.
/// Inject IStringLocalizer&lt;CustomerErrors&gt; to access these resources.
/// Resource files: Resources/CustomerErrors.resx (fr default), Resources/CustomerErrors.en.resx
///
/// Keys follow <c>Aggregate.Reason</c> and are the human-facing counterpart of the
/// MAJUSCULES_SNAKE codes returned in <c>Result.Error</c>: the code is the machine
/// contract consumed by the API client, the resource is what the operator reads.
/// </summary>
public sealed class CustomerErrors;
