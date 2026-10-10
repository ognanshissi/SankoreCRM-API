namespace Sankore.Modules.Customers.Features.Holdings.UpdateSpecifics;

using MediatR;
using Sankore.Modules.Customers.Features.Holdings.Shared;
using Sankore.Shared.Kernel;

internal sealed class UpdateHoldingSpecificsHandler(HoldingWriteContext context)
    : IRequestHandler<UpdateHoldingSpecificsCommand, Result>
{
    public async Task<Result> Handle(UpdateHoldingSpecificsCommand cmd, CancellationToken ct)
    {
        var loaded = await context.LoadAsync(cmd.HoldingId, ct);
        if (loaded.IsFailure)
            return Result.Fail(loaded.Error!);

        var specifics = HoldingMapping.ToSpecifics(cmd.Specifics);
        if (specifics.IsFailure)
            return Result.Fail(specifics.Error!);

        var updated = loaded.Value.UpdateSpecifics(specifics.Value, context.Clock.GetUtcNow());
        if (updated.IsFailure)
            return updated;

        await context.SaveAsync(ct);
        return Result.Ok();
    }
}
