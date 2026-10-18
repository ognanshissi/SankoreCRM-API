using Swashbuckle.AspNetCore.Swagger;

namespace Sankore.Admin.Infrastructure.OpenApi;

/// <summary>
/// Writes this host's OpenAPI document to a file and exits — the admin twin of Sankore.Api's
/// emitter, for the same reason: the front-end client <c>@sankore/crm-admin-api</c> is generated
/// from <c>SankoreFront/swaggers/sankore-crm-admin-api-swagger.json</c>, and producing it must not
/// need a migrated database, a signing key or the Swagger Basic credentials.
///
/// <code>dotnet run --project src/Bootstrapper/Sankore.Admin -- --emit-openapi &lt;ABSOLUTE path&gt;</code>
///
/// A relative path resolves against the PROJECT directory, not the shell's.
/// </summary>
internal static class OpenApiEmitter
{
    private const string Flag = "--emit-openapi";
    private const string DocumentName = "v1";
    private const string DefaultOutputPath = "openapi.json";

    internal static string? OutputPathFrom(string[] args)
    {
        var index = Array.IndexOf(args, Flag);
        if (index < 0) return null;

        var next = index + 1 < args.Length ? args[index + 1] : null;
        return string.IsNullOrWhiteSpace(next) || next.StartsWith('-') ? DefaultOutputPath : next;
    }

    internal static async Task WriteAsync(WebApplication app, string outputPath)
    {
        // Started, then stopped: the endpoint data sources only materialise when the host starts,
        // and without them Swashbuckle writes a valid document with zero paths.
        await app.StartAsync();
        try
        {
            var swagger = app.Services.GetRequiredService<ISwaggerProvider>().GetSwagger(DocumentName);
            if (swagger.Paths.Count == 0)
                throw new InvalidOperationException(
                    "The OpenAPI document came back with no paths; writing it would hand the client generator an empty API.");

            var full = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);

            await using (var stream = File.Create(full))
            await using (var writer = new StreamWriter(stream))
            {
                // 3.0, like the CRM document the same generator already consumes.
                swagger.SerializeAsV3(new Microsoft.OpenApi.Writers.OpenApiJsonWriter(writer));
                await writer.FlushAsync();
            }

            app.Logger.LogInformation(
                "OpenAPI document written to {OutputPath} ({PathCount} paths)", full, swagger.Paths.Count);
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
