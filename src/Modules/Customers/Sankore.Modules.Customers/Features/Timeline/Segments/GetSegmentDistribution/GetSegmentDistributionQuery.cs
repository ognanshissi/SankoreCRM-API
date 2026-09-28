namespace Sankore.Modules.Customers.Features.Timeline.Segments.GetSegmentDistribution;

using MediatR;
using Sankore.Shared.Kernel;

/// <summary>How the tenant's live clients are spread across segments (US-M01-BE-27).</summary>
public sealed record GetSegmentDistributionQuery : IRequest<Result<SegmentDistributionDto>>;

/// <param name="TotalClients">Live clients in the caller's perimeter (archived and merged excluded).</param>
/// <param name="Buckets">One entry per segment, largest first; unsegmented clients come last.</param>
public sealed record SegmentDistributionDto(int TotalClients, IReadOnlyList<SegmentBucketDto> Buckets);

/// <param name="SegmentCode">Segment code, or <c>null</c> for clients no rule has classified yet.</param>
/// <param name="SharePercent">Share of <c>TotalClients</c>, rounded to one decimal.</param>
public sealed record SegmentBucketDto(string? SegmentCode, int ClientCount, decimal SharePercent);
