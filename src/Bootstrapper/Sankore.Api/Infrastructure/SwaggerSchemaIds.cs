namespace Sankore.Api.Infrastructure;

using System.Collections.Concurrent;
using System.Reflection;

/// <summary>
/// Schema ids for the OpenAPI document: the SHORT type name, except where two modules happen to
/// declare the same one — those get their module's name in front.
///
/// <para>
/// <b>Why this exists.</b> Swashbuckle's default id is the short name, so the first time two modules
/// declare a <c>PersonIdentityDto</c> the whole document generation throws:
/// <c>Can't use schemaId "$PersonIdentityDto" for type … The same schemaId is already used for
/// type …</c>. That is a start-up failure, not a bad schema — and it is caught only by running the
/// host, which is exactly what <c>--emit-openapi</c> is for.
/// </para>
///
/// <para>
/// <b>Why not simply fully-qualify everything.</b> The id IS the generated client's class name, so
/// switching globally would rename every model in the Angular client at once — hundreds of
/// breaking renames to fix one collision. This renames ONLY the ambiguous short names, so a
/// document generated before and after is identical except for the types that actually clashed.
/// </para>
///
/// <para>
/// Two modules sharing a DTO NAME is legitimate: a client's household and a lead's household are
/// different wire contracts for the same idea, and merging them would make one module's API a
/// dependency of the other's. What is not legitimate is a document that cannot be generated.
/// </para>
/// </summary>
internal static class SwaggerSchemaIds
{
    /// <summary>
    /// Short names declared by more than one module type, computed once from the loaded module
    /// assemblies. Built eagerly rather than discovered as Swashbuckle walks types, because the
    /// first of two colliding types has already been registered under the short name by the time
    /// the second arrives.
    /// </summary>
    private static readonly Lazy<HashSet<string>> Ambiguous = new(FindAmbiguousNames);

    private static readonly ConcurrentDictionary<Type, string> Cache = new();

    internal static string For(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return Cache.GetOrAdd(type, t =>
        {
            var name = Name(t);

            if (!Ambiguous.Value.Contains(name))
                return name;

            // "CustomersPersonIdentityDto" / "LeadsPersonIdentityDto" — the module, not the full
            // namespace, because the full one produces a class name nobody wants to type.
            var module = ModuleOf(t);
            return module is null ? name : module + name;
        });
    }

    /// <summary>
    /// A readable short name, with generics flattened the way Swashbuckle does
    /// (<c>PagedResult&lt;HoldingDto&gt;</c> → <c>PagedResultOfHoldingDto</c>).
    /// </summary>
    private static string Name(Type type)
    {
        if (!type.IsGenericType)
            return type.Name;

        var stem = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
        var args = string.Join("And", type.GetGenericArguments().Select(Name));
        return $"{stem}Of{args}";
    }

    /// <summary>
    /// The module segment of <c>Sankore.Modules.&lt;Module&gt;…</c>, or null for anything else
    /// (the shared kernel, the bootstrapper, a framework type).
    /// </summary>
    private static string? ModuleOf(Type type)
    {
        var parts = (type.Namespace ?? string.Empty).Split('.');

        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i] == "Modules")
                return parts[i + 1];
        }

        return null;
    }

    private static HashSet<string> FindAmbiguousNames()
    {
        var byName = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic
                || assembly.GetName().Name is not { } assemblyName
                || !assemblyName.StartsWith("Sankore.", StringComparison.Ordinal))
                continue;

            Type[] types;
            try
            {
                types = assembly.GetExportedTypes();
            }
            catch (ReflectionTypeLoadException)
            {
                // A module whose dependencies are not all loaded yet contributes nothing rather
                // than failing the boot: the worst case is a collision this misses, which surfaces
                // as the same explicit Swashbuckle error it does today.
                continue;
            }

            foreach (var type in types)
            {
                if (type.Namespace is null || !type.IsPublic || type.IsNested)
                    continue;

                if (!byName.TryGetValue(type.Name, out var namespaces))
                    byName[type.Name] = namespaces = [];

                namespaces.Add(type.Namespace);
            }
        }

        return [.. byName.Where(e => e.Value.Count > 1).Select(e => e.Key)];
    }
}
