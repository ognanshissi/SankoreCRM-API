namespace Sankore.Modules.Customers.Features.Clients.Shared;

/// <summary>
/// The postal address as the operator types it, before the module turns it into a
/// single encrypted <c>ClientContactPoint</c> of type <c>Address</c>.
///
/// A client's address is NOT an EF owned value object here (unlike
/// <c>Sankore.Shared.Kernel.Address</c> used by Administration): it is protected
/// data, so it lives encrypted in one column with a blind index next to it. That
/// forces a single-line canonical rendering — hence <see cref="ToSingleLine"/>.
/// </summary>
public sealed record PostalAddressInput(
    string? Street,
    string? City,
    string? State,
    string? Country,
    string? ZipCode)
{
    /// <summary>True when every component is blank — nothing worth storing.</summary>
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Street)
        && string.IsNullOrWhiteSpace(City)
        && string.IsNullOrWhiteSpace(State)
        && string.IsNullOrWhiteSpace(Country)
        && string.IsNullOrWhiteSpace(ZipCode);

    /// <summary>
    /// Comma-separated, blank components dropped: the exact string that gets
    /// encrypted and (after normalization) blind-indexed. Deterministic so that the
    /// same address typed twice yields the same index.
    /// </summary>
    public string ToSingleLine()
    {
        var parts = new List<string>(5);
        Append(parts, Street);
        Append(parts, ZipCode);
        Append(parts, City);
        Append(parts, State);
        Append(parts, Country);

        return string.Join(", ", parts);

        static void Append(List<string> target, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) target.Add(value.Trim());
        }
    }

    /// <summary>Same as <see cref="ToSingleLine"/> but null when nothing was provided.</summary>
    public string? ToSingleLineOrNull()
    {
        if (IsEmpty) return null;

        var line = ToSingleLine();
        return string.IsNullOrWhiteSpace(line) ? null : line;
    }

    /// <summary>Kept so callers can build the line without allocating a list twice.</summary>
    public override string ToString() => ToSingleLine();
}
