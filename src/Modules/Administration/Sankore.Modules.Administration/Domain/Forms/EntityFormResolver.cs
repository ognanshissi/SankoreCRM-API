namespace Sankore.Modules.Administration.Domain.Forms;

using Sankore.Modules.Administration.PublicApi.Forms;

/// <summary>
/// The form a tenant actually gets: the native catalogue, overridden by what the tenant stored, plus
/// its custom fields. The one read path — the editor, the creation form, the fiche and the value
/// validation of every module all go through it.
/// </summary>
internal static class EntityFormResolver
{
    /// <summary>Order given to a native field the tenant's layout does not mention yet, so it lands last.</summary>
    private const int UnplacedOrder = 1000;

    public static EntityFormDto Resolve(FormEntity entity, int version, FormLayoutDocument? stored)
    {
        var catalog = NativeFormCatalog.For(entity);

        if (stored is null || version == 0)
            return new EntityFormDto(entity, 0, catalog.Sections, catalog.Fields);

        var sections = stored.Sections.OrderBy(s => s.Order).ToList();
        var sectionKeys = sections.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        var storedByKey = stored.Fields.ToDictionary(f => f.Key, StringComparer.Ordinal);

        var fields = new List<FormField>(catalog.Fields.Count + stored.Fields.Count);

        foreach (var native in catalog.Fields)
        {
            if (storedByKey.TryGetValue(native.Key, out var o))
            {
                var locked = native.IsRequiredLocked;
                var required = locked || o.IsRequired;

                // Type, options, limits and conditions stay the catalogue's: they describe the
                // entity's property, not the tenant's taste.
                fields.Add(native with
                {
                    Label = string.IsNullOrWhiteSpace(o.Label) ? native.Label : o.Label,
                    HelpText = o.HelpText,
                    Placeholder = o.Placeholder,
                    SectionKey = o.SectionKey is not null && sectionKeys.Contains(o.SectionKey) ? o.SectionKey : null,
                    Order = o.Order,
                    IsRequired = required,
                    QuickAdd = required || o.QuickAdd,
                    IsHidden = !required && o.IsHidden,
                });
            }
            else
            {
                // A native field added to the catalogue after this tenant saved its layout.
                fields.Add(native with
                {
                    SectionKey = native.SectionKey is not null && sectionKeys.Contains(native.SectionKey)
                        ? native.SectionKey
                        : null,
                    Order = UnplacedOrder + native.Order,
                });
            }
        }

        foreach (var custom in stored.Fields)
        {
            if (catalog.Find(custom.Key) is not null) continue;

            fields.Add(custom with
            {
                SectionKey = custom.SectionKey is not null && sectionKeys.Contains(custom.SectionKey)
                    ? custom.SectionKey
                    : null,
                IsNative = false,
                IsRequiredLocked = false,
            });
        }

        return new EntityFormDto(entity, version, sections, fields.OrderBy(f => f.Order).ToList());
    }
}
