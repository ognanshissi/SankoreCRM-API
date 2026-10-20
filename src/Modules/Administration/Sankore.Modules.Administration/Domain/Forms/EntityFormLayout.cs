namespace Sankore.Modules.Administration.Domain.Forms;

using Sankore.Modules.Administration.PublicApi.Forms;
using Sankore.Shared.Kernel;

/// <summary>
/// What a tenant stored for one entity's form: its sections, its overrides of the native fields and
/// its custom fields. Merged with <see cref="NativeFormCatalog"/> on every read by
/// <see cref="EntityFormResolver"/>.
/// </summary>
/// <remarks>
/// One jsonb document rather than section and field tables: the editor saves the whole layout at
/// once, every reader reads it whole, and nothing ever queries one field of it in SQL. The custom
/// VALUES are the queryable part, and they live on the leads, clients and products themselves.
/// </remarks>
public sealed record FormLayoutDocument(IReadOnlyList<FormSection> Sections, IReadOnlyList<FormField> Fields)
{
    public static FormLayoutDocument Empty => new([], []);
}

public sealed class EntityFormLayout
{
    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public FormEntity Entity { get; private set; }

    /// <summary>Incremented on every save; the editor sends back the one it read.</summary>
    public int Version { get; private set; }

    public FormLayoutDocument Layout { get; private set; } = FormLayoutDocument.Empty;
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid UpdatedBy { get; private set; }

    private EntityFormLayout() { } // EF Core

    public static EntityFormLayout Create(Guid tenantId, FormEntity entity)
        => new() { Id = Guid.NewGuid(), TenantId = tenantId, Entity = entity, Version = 0 };

    /// <summary>
    /// Replaces the layout after <see cref="EntityFormRules.Normalise"/> accepted it.
    /// </summary>
    /// <param name="expectedVersion">
    /// The version the editor loaded. A mismatch is refused rather than merged: two administrators
    /// moving fields at once would otherwise each silently undo the other's work.
    /// </param>
    public Result Replace(
        FormSectionsAndFields incoming, int expectedVersion, Guid updatedBy, DateTimeOffset now)
    {
        if (expectedVersion != Version)
            return Result.Fail("FORM_VERSION_CONFLICT");

        var normalised = EntityFormRules.Normalise(Entity, Version == 0 ? null : Layout, incoming);
        if (normalised.IsFailure)
            return Result.Fail(normalised.Error!);

        Layout = normalised.Value;
        Version++;
        UpdatedAt = now;
        UpdatedBy = updatedBy;
        return Result.Ok();
    }
}

/// <summary>What the editor posts: the sections and every field it shows, native or custom.</summary>
public sealed record FormSectionsAndFields(IReadOnlyList<FormSection> Sections, IReadOnlyList<FormField> Fields);
