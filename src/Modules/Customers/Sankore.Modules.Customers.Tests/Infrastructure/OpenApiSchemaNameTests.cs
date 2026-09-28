namespace Sankore.Modules.Customers.Tests.Infrastructure;

using System.Reflection;
using FluentAssertions;
using Xunit;

/// <summary>
/// Swashbuckle keys every OpenAPI component by the type's SIMPLE name. In a vertical-slice
/// module each slice owns its own DTOs, so two slices picking the same obvious name is not a
/// far-fetched scenario — it is the default outcome. When it happens, Swagger generation
/// throws at the first request:
///
/// <code>
/// Can't use schemaId "$ClientStatusHistoryDto" for type
/// "...Features.Clients.Shared.ClientStatusHistoryDto". The same schemaId is already used for
/// type "...Features.Lifecycle.GetStatusHistory.ClientStatusHistoryDto"
/// </code>
///
/// That is a run-time failure of the whole API surface, caught by no compiler and by no
/// handler test — every endpoint stops being documented and Swagger UI 500s. This test turns
/// it into a build-time failure instead.
///
/// If a collision is reported, rename one of the two types rather than reaching for
/// <c>CustomSchemaIds</c>: qualifying ids globally would rename every model in the generated
/// TypeScript client, which is a far larger blast radius than one DTO.
/// </summary>
public sealed class OpenApiSchemaNameTests
{
    private static readonly Assembly ModuleAssembly = typeof(CustomersModule).Assembly;

    /// <summary>
    /// Public types under <c>Features/</c> — the ones that can end up in a request or response
    /// body and therefore in the OpenAPI document. Nested and generic definitions are excluded:
    /// Swashbuckle derives their ids from the enclosing or closed type.
    /// </summary>
    private static IEnumerable<Type> CandidateSchemaTypes() =>
        ModuleAssembly.GetTypes()
            .Where(t => t.IsPublic
                        && !t.IsNested
                        && !t.IsGenericTypeDefinition
                        && !t.IsInterface
                        && t.Namespace?.Contains(".Features.", StringComparison.Ordinal) == true);

    [Fact]
    public void No_two_public_feature_types_share_a_simple_name()
    {
        var collisions = CandidateSchemaTypes()
            .GroupBy(t => t.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(" | ", g.Select(t => t.FullName))}")
            .ToList();

        collisions.Should().BeEmpty(
            "Swashbuckle keys OpenAPI components by simple type name, so two same-named public "
            + "types make Swagger generation throw for the whole API at the first request");
    }
}
