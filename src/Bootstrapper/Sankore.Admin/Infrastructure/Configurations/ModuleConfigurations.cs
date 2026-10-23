using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sankore.Admin.Domain;

namespace Sankore.Admin.Infrastructure.Configurations;

public sealed class ModuleDefinitionConfiguration : IEntityTypeConfiguration<ModuleDefinition>
{
    public void Configure(EntityTypeBuilder<ModuleDefinition> builder)
    {
        builder.HasKey(m => m.Code);
        builder.Property(m => m.Code).HasMaxLength(ModuleDefinition.CodeMaxLength);
        builder.Property(m => m.Label).HasMaxLength(ModuleDefinition.LabelMaxLength).IsRequired();
        builder.Property(m => m.Description).HasMaxLength(ModuleDefinition.DescriptionMaxLength).IsRequired();
        builder.Property(m => m.MonthlyPrice).HasPrecision(18, 2);
        builder.Property(m => m.Currency).HasMaxLength(ModuleDefinition.CurrencyLength).IsRequired();
    }
}

public sealed class TenantModuleSubscriptionConfiguration : IEntityTypeConfiguration<TenantModuleSubscription>
{
    /// <summary>Named so the handlers can recognise its violation among other unique errors.</summary>
    public const string OpenRowIndex = "ux_tenant_module_open";

    public void Configure(EntityTypeBuilder<TenantModuleSubscription> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Ignore(s => s.IsOpen);

        builder.Property(s => s.ModuleCode).HasMaxLength(ModuleDefinition.CodeMaxLength).IsRequired();

        builder.Property(s => s.Status)
            .IsRequired()
            .HasMaxLength(20)
            .HasConversion(v => v.ToString(), v => Enum.Parse<ModuleSubscriptionStatus>(v));

        builder.Property(s => s.Source)
            .IsRequired()
            .HasMaxLength(20)
            .HasConversion(v => v.ToString(), v => Enum.Parse<ModuleSubscriptionSource>(v));

        builder.Property(s => s.RequestedByEmail).HasMaxLength(TenantModuleSubscription.EmailMaxLength);
        builder.Property(s => s.DeactivatedByEmail).HasMaxLength(TenantModuleSubscription.EmailMaxLength);
        builder.Property(s => s.AcceptedMonthlyPrice).HasPrecision(18, 2);
        builder.Property(s => s.AcceptedCurrency).HasMaxLength(ModuleDefinition.CurrencyLength);
        builder.Property(s => s.DecisionReason).HasMaxLength(TenantModuleSubscription.ReasonMaxLength);

        // Logical reference only — no physical FK, like TenantDomains.
        builder.HasIndex(s => s.TenantId)
            .HasDatabaseName("ix_tenant_module_subscriptions_tenant_id");

        builder.HasIndex(s => s.Status)
            .HasDatabaseName("ix_tenant_module_subscriptions_status");

        // At most one OPEN row (a pending request or an active subscription) per tenant and module.
        // This index is the guarantee; the handlers' prior read only produces a nicer error.
        builder.HasIndex(s => new { s.TenantId, s.ModuleCode })
            .IsUnique()
            .HasFilter("\"Status\" IN ('Requested', 'Active')")
            .HasDatabaseName(OpenRowIndex);
    }
}
