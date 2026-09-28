namespace Sankore.Modules.Customers.Tests.Features.LeadConversion;

using FluentAssertions;
using MediatR;
using NSubstitute;
using Sankore.Modules.Customers.Features.LeadConversion;
using Sankore.Modules.Customers.Features.LeadConversion.CreateClientFromLead;
using Sankore.Modules.Customers.PublicApi;
using Sankore.Shared.Kernel;
using Xunit;

/// <summary>
/// The service is a one-line seam, but the line that matters is <i>which</i> path the facade
/// takes: dispatching through MediatR is what puts validation, the transaction and the audit
/// entry around the conversion. These tests pin the dispatch and the field-by-field mapping so a
/// later refactor cannot quietly bypass the pipeline or drop a field the lead collected.
/// </summary>
public sealed class LeadConversionServiceTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AgencyId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid UserId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid LeadId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid RequestedClientId = Guid.Parse("dddddddd-0000-0000-0000-000000000009");

    private static CreateFromLeadRequest Request() => new(
        TenantId: TenantId,
        LeadId: LeadId,
        AgencyId: AgencyId,
        ConvertedByUserId: UserId,
        FirstName: "Awa",
        LastName: "Kone",
        LegalName: null,
        Gender: "Female",
        DateOfBirth: new DateOnly(1990, 3, 17),
        Nationality: "CI",
        PhoneNumber: "+2250708091810",
        Email: "awa.kone@example.ci",
        IdentityDocumentType: "NationalIdCard",
        IdentityDocumentNumber: "CI-0123456789",
        Profession: "Commerçante",
        PreferredLanguage: "fr",
        RequestedClientId: RequestedClientId);

    [Fact]
    public async Task Dispatches_the_command_through_mediator_and_returns_its_result()
    {
        var expected = Result.Ok(new CreateFromLeadResult(RequestedClientId, "AG000001-2026-000042", false));
        var sender = Substitute.For<ISender>();
        sender
            .Send(Arg.Any<CreateClientFromLeadCommand>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        var service = new LeadConversionService(sender);

        var result = await service.CreateFromLeadAsync(Request(), CancellationToken.None);

        result.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task Copies_every_field_of_the_request_onto_the_audited_command()
    {
        CreateClientFromLeadCommand? dispatched = null;
        var sender = Substitute.For<ISender>();
        sender
            .Send(Arg.Do<CreateClientFromLeadCommand>(c => dispatched = c), Arg.Any<CancellationToken>())
            .Returns(Result.Ok(new CreateFromLeadResult(RequestedClientId, "AG000001-2026-000042", false)));

        await new LeadConversionService(sender).CreateFromLeadAsync(Request(), CancellationToken.None);

        var request = Request();
        dispatched.Should().NotBeNull();
        dispatched!.TenantId.Should().Be(request.TenantId);
        dispatched.LeadId.Should().Be(request.LeadId);
        dispatched.AgencyId.Should().Be(request.AgencyId);
        dispatched.ConvertedByUserId.Should().Be(request.ConvertedByUserId);
        dispatched.FirstName.Should().Be(request.FirstName);
        dispatched.LastName.Should().Be(request.LastName);
        dispatched.LegalName.Should().Be(request.LegalName);
        dispatched.Gender.Should().Be(request.Gender);
        dispatched.DateOfBirth.Should().Be("1990-03-17");
        dispatched.BirthDate.Should().Be(request.DateOfBirth);
        dispatched.Nationality.Should().Be(request.Nationality);
        dispatched.PhoneNumber.Should().Be(request.PhoneNumber);
        dispatched.Email.Should().Be(request.Email);
        dispatched.IdentityDocumentType.Should().Be(request.IdentityDocumentType);
        dispatched.IdentityDocumentNumber.Should().Be(request.IdentityDocumentNumber);
        dispatched.Profession.Should().Be(request.Profession);
        dispatched.PreferredLanguage.Should().Be(request.PreferredLanguage);
        dispatched.RequestedClientId.Should().Be(request.RequestedClientId);
    }

    [Fact]
    public void Marks_the_sensitive_fields_so_the_audit_trail_redacts_them()
    {
        // The audit log serializes every ICommand with SanitizedJsonSerializer, which only
        // redacts properties carrying [SensitiveData]. CreateFromLeadRequest carries none, which
        // is exactly why the command copies the fields instead of nesting the record.
        var command = CreateClientFromLeadCommand.From(Request());

        var json = Sankore.Shared.Infrastructure.Behaviors.SanitizedJsonSerializer.Serialize(command);

        json.Should().NotContain("CI-0123456789");
        json.Should().NotContain("+2250708091810");
        json.Should().NotContain("awa.kone@example.ci");
        json.Should().NotContain("1990-03-17");

        // Non-sensitive context is still auditable — a redacted-everything entry is useless.
        json.Should().Contain(LeadId.ToString());
        json.Should().Contain("Awa");
    }
}
