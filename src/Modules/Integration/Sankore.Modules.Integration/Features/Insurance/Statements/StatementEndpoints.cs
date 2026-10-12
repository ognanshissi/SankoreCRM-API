namespace Sankore.Modules.Integration.Features.Insurance.Statements;

using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Sankore.Modules.Integration.Features.Insurance.Statements.FinaliseStatement;
using Sankore.Shared.Infrastructure.Extensions;
using Sankore.Shared.Kernel;

/// <summary>
/// ASS-10: reading a month's bordereau, exporting it, and closing it.
///
/// <para>
/// <b>There is no generation endpoint.</b> The nightly job builds the previous month, and a draft
/// is rebuilt by the same job on its next run — exposing a "regenerate" button would let an
/// operator rebuild a month that a colleague is in the middle of reconciling, and the figures
/// would move under them. Finalising is the one state change this area offers.
/// </para>
/// </summary>
internal static class StatementEndpoints
{
    internal static IEndpointRouteBuilder MapStatements(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("integration/insurance/statements").WithTags("Integration");

        group.MapGet(string.Empty, List)
            .WithName("ListInsurerStatements")
            .WithSummary("The tenant's monthly bordereaux")
            .WithDescription(
                "Newest period first. `period` is a month written `YYYY-MM`; `connectionId` "
                + "narrows to one insurer. The counts and the totals are the statement's own "
                + "stored figures, never a sum recomputed over today's policies — a finalised "
                + "document renders the same numbers in a year. "
                + "Requires permission: Ins.Statement.View.")
            .RequireAuthorization(Permissions.CanViewInsuranceStatement.Code)
            .Produces<IReadOnlyList<StatementDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapGet("{statementId:guid}", Get)
            .WithName("GetInsurerStatement")
            .WithSummary("One bordereau with its printed detail")
            .WithDescription(
                "Each line carries the policy number, the insurer's product code, the CBS "
                + "reference and the commission RATE as they stood when the line was written — a "
                + "bordereau is a justification document, and one that re-rendered from today's "
                + "catalogue would change a figure somebody has already signed. "
                + "Requires permission: Ins.Statement.View.")
            .RequireAuthorization(Permissions.CanViewInsuranceStatement.Code)
            .Produces<StatementDetailDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapGet("{statementId:guid}/export.csv", Export)
            .WithName("ExportInsurerStatementCsv")
            .WithSummary("The bordereau as a CSV")
            .WithDescription(
                "Semicolon-separated and UTF-8 with a BOM, which is what Excel on a French "
                + "locale needs to read accented product names correctly. Carries the detail and "
                + "a TOTAL line, because the first thing an insurer's back office checks is that "
                + "the two agree. No customer identity travels — only the opaque CRM id the two "
                + "systems reconcile on. "
                + "Requires permission: Ins.Statement.View.")
            .RequireAuthorization(Permissions.CanViewInsuranceStatement.Code)
            .Produces(StatusCodes.Status200OK, contentType: StatementCsv.ContentType)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .WithOpenApi()
            .WithTenantHeader();

        group.MapPost("{statementId:guid}/finalisation", Finalise)
            .WithName("FinaliseInsurerStatement")
            .WithSummary("Close the month's bordereau")
            .WithDescription(
                "From here the figures are the ones the institution stands behind: the nightly "
                + "generation can no longer rebuild this month, which is what stops a "
                + "regeneration from silently restating a month already justified to the insurer. "
                + "A statement already closed is a 409. "
                + "Requires permission: Ins.Statement.View.")
            .RequireAuthorization(Permissions.CanViewInsuranceStatement.Code)
            .Produces<StatementDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .WithOpenApi()
            .WithTenantHeader();

        return app;
    }

    private static async Task<IResult> List(
        ISender sender, CancellationToken ct, Guid? connectionId = null, string? period = null)
    {
        var result = await sender.Send(new ListStatementsQuery(connectionId, period), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.BadRequest(new { error = result.Error });
    }

    private static async Task<IResult> Get(
        Guid statementId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetStatementQuery(statementId), ct);

        return result.IsSuccess
            ? Results.Ok(result.Value)
            : Results.NotFound(new { error = result.Error });
    }

    private static async Task<IResult> Export(
        Guid statementId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new GetStatementQuery(statementId), ct);

        if (result.IsFailure)
            return Results.NotFound(new { error = result.Error });

        return Results.File(
            StatementCsv.Render(result.Value!),
            StatementCsv.ContentType,
            StatementCsv.FileName(result.Value!.Statement));
    }

    private static async Task<IResult> Finalise(
        Guid statementId, ISender sender, CancellationToken ct)
    {
        var result = await sender.Send(new FinaliseStatementCommand(statementId), ct);

        if (result.IsSuccess) return Results.Ok(result.Value);

        var error = result.Error!;

        if (error == StatementErrors.NotFound)
            return Results.NotFound(new { error });

        if (error == StatementErrors.AlreadyClosed)
            return Results.Conflict(new { error });

        return Results.BadRequest(new { error });
    }
}
