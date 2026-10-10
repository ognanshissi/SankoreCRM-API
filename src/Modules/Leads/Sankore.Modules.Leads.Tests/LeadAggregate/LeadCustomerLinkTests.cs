namespace Sankore.Modules.Leads.Tests.LeadAggregate;

using FluentAssertions;
using Sankore.Modules.Leads.Domain;
using Sankore.Modules.Leads.Domain.Events;
using Sankore.Shared.Kernel.ValueObject;
using Xunit;

/// <summary>Covers the existing-customer link on the aggregate (BNF-B-22).</summary>
public sealed class LeadCustomerLinkTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Customer = Guid.NewGuid();

    private static Lead Capture(Guid? customerId = null, bool automatic = false, string? reason = null)
        => Lead.Capture(
            tenantId: Tenant,
            fullName: "Aya Kouassi",
            phoneNumber: "+2250700000001",
            source: LeadSource.Agency,
            interestedProduct: "Crédit commerçant",
            preferredLanguage: "fr",
            location: new GeoPoint(5.3, -4.0),
            preferredAgencyId: null,
            clock: TimeProvider.System,
            customerId: customerId,
            customerLinkIsAutomatic: automatic,
            customerLinkReason: reason);

    // ── Kind is derived ───────────────────────────────────────────────────

    [Fact]
    public void An_unattached_lead_is_a_new_prospect()
    {
        var lead = Capture();

        lead.CustomerId.Should().BeNull();
        lead.Kind.Should().Be(LeadKind.NewProspect);
    }

    [Fact]
    public void Kind_follows_the_customer_id_in_both_directions()
    {
        // Derived, never stored — so there is no way for the two to disagree, and the conversion
        // branches on Kind.
        var lead = Capture();

        lead.AttachToCustomer(Customer, isAutomatic: true, reason: null);
        lead.Kind.Should().Be(LeadKind.ExistingCustomer);

        lead.DetachFromCustomer("wrong person");
        lead.Kind.Should().Be(LeadKind.NewProspect);
    }

    // ── Attaching ─────────────────────────────────────────────────────────

    [Fact]
    public void An_automatic_attachment_needs_no_reason()
    {
        // The matcher's rule IS the reason — a unique, person-identifying match.
        var lead = Capture();

        lead.AttachToCustomer(Customer, isAutomatic: true, reason: null)
            .IsSuccess.Should().BeTrue();

        lead.CustomerId.Should().Be(Customer);
        lead.CustomerLinkIsAutomatic.Should().BeTrue();
        lead.CustomerLinkedAt.Should().NotBeNull();
    }

    [Fact]
    public void A_manual_attachment_without_a_reason_is_refused()
    {
        var lead = Capture();

        var result = lead.AttachToCustomer(Customer, isAutomatic: false, reason: "  ");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("CUSTOMER_LINK_REASON_REQUIRED");
        lead.CustomerId.Should().BeNull();
    }

    [Fact]
    public void An_empty_customer_id_is_refused()
    {
        var result = Capture().AttachToCustomer(Guid.Empty, isAutomatic: true, reason: null);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("CUSTOMER_ID_REQUIRED");
    }

    [Fact]
    public void Attaching_raises_the_link_event()
    {
        var lead = Capture();
        lead.ClearDomainEvents();

        lead.AttachToCustomer(Customer, isAutomatic: false, reason: "client reconnu au guichet");

        lead.DomainEvents.Should().ContainSingle()
            .Which.Should().BeOfType<LeadLinkedToCustomerDomainEvent>()
            .Which.CustomerId.Should().Be(Customer);
    }

    [Fact]
    public void Re_attaching_to_the_same_customer_is_a_no_op()
    {
        // A replayed matcher or a double-clicked button must not overwrite a MANUAL decision with
        // an automatic one, nor re-raise the event.
        var lead = Capture();
        lead.AttachToCustomer(Customer, isAutomatic: false, reason: "reconnu au guichet");
        lead.ClearDomainEvents();

        lead.AttachToCustomer(Customer, isAutomatic: true, reason: null).IsSuccess.Should().BeTrue();

        lead.CustomerLinkIsAutomatic.Should().BeFalse("the manual decision stands");
        lead.CustomerLinkReason.Should().Be("reconnu au guichet");
        lead.DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public void Attaching_to_a_different_customer_replaces_the_link_and_raises_again()
    {
        var other = Guid.NewGuid();
        var lead = Capture();
        lead.AttachToCustomer(Customer, isAutomatic: true, reason: null);
        lead.ClearDomainEvents();

        lead.AttachToCustomer(other, isAutomatic: false, reason: "corrigé par le superviseur")
            .IsSuccess.Should().BeTrue();

        lead.CustomerId.Should().Be(other);
        lead.CustomerLinkReason.Should().Be("corrigé par le superviseur");
        lead.DomainEvents.Should().ContainSingle();
    }

    [Fact]
    public void A_converted_lead_cannot_be_re_attached()
    {
        var lead = Capture();
        lead.Convert(Guid.NewGuid());

        lead.AttachToCustomer(Customer, isAutomatic: true, reason: null)
            .IsFailure.Should().BeTrue();
    }

    // ── Capturing already attached ────────────────────────────────────────

    [Fact]
    public void Capturing_with_a_customer_records_the_link_and_raises_the_event()
    {
        var lead = Capture(Customer, automatic: true, reason: "correspondance unique");

        lead.CustomerId.Should().Be(Customer);
        lead.CustomerLinkIsAutomatic.Should().BeTrue();
        lead.CustomerLinkReason.Should().Be("correspondance unique");
        lead.DomainEvents.Should().ContainItemsAssignableTo<LeadLinkedToCustomerDomainEvent>();
    }

    [Fact]
    public void Capturing_without_a_customer_stores_no_link_metadata()
    {
        // A reason or a timestamp on an unattached lead would read as "there was a link once".
        var lead = Capture(customerId: null, automatic: true, reason: "ignored");

        lead.CustomerLinkIsAutomatic.Should().BeFalse();
        lead.CustomerLinkReason.Should().BeNull();
        lead.CustomerLinkedAt.Should().BeNull();
        lead.DomainEvents.Should().NotContainItemsAssignableTo<LeadLinkedToCustomerDomainEvent>();
    }

    // ── Detaching ─────────────────────────────────────────────────────────

    [Fact]
    public void Detaching_an_unattached_lead_is_refused()
    {
        var result = Capture().DetachFromCustomer("no reason to");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("LEAD_NOT_LINKED_TO_CUSTOMER");
    }

    [Fact]
    public void Detaching_without_a_reason_is_refused()
    {
        var lead = Capture(Customer, automatic: true);

        var result = lead.DetachFromCustomer("  ");

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("CUSTOMER_LINK_REASON_REQUIRED");
        lead.CustomerId.Should().Be(Customer, "a refused detach leaves the link alone");
    }

    [Fact]
    public void Detaching_keeps_the_reason_and_raises_the_event()
    {
        var lead = Capture(Customer, automatic: true);
        lead.ClearDomainEvents();

        lead.DetachFromCustomer("homonyme — ce n'est pas le même client").IsSuccess.Should().BeTrue();

        lead.CustomerId.Should().BeNull();
        lead.CustomerLinkIsAutomatic.Should().BeFalse();
        lead.CustomerLinkedAt.Should().BeNull();
        // The reason for DETACHING is the explanation that matters most afterwards.
        lead.CustomerLinkReason.Should().Be("homonyme — ce n'est pas le même client");

        lead.DomainEvents.Should().ContainSingle()
            .Which.Should().BeOfType<LeadDetachedFromCustomerDomainEvent>()
            .Which.PreviousCustomerId.Should().Be(Customer);
    }
}
