namespace Sankore.Modules.Leads.Infrastructure.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Modules.Leads.Domain;

internal sealed class AgentObjectiveConfiguration : IEntityTypeConfiguration<AgentObjective>
{
    public void Configure(EntityTypeBuilder<AgentObjective> builder)
    {
        builder.ToTable("agent_objectives");
        builder.HasKey(o => o.Id);

        // One row per agent and month; the effective objective is the latest month ≤ the one read.
        builder.HasIndex(o => new { o.TenantId, o.AgentId, o.Month }).IsUnique();
    }
}
