namespace Sankore.Modules.Kyc.Features.Files.CreateKycFile;

using FluentValidation;

internal sealed class CreateKycFileValidator : AbstractValidator<CreateKycFileCommand>
{
    public CreateKycFileValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.CustomerId).NotEmpty();

        // Not InitiatedBy: an automatic trigger may legitimately carry Guid.Empty when the
        // originating event has no human actor, and refusing the command would leave the customer
        // with no KYC file at all — the one outcome KYC-B-01 exists to prevent.
    }
}
