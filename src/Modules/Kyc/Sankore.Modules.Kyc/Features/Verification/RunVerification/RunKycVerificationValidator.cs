namespace Sankore.Modules.Kyc.Features.Verification.RunVerification;

using FluentValidation;

internal sealed class RunKycVerificationValidator : AbstractValidator<RunKycVerificationCommand>
{
    public RunKycVerificationValidator()
    {
        RuleFor(x => x.TenantId).NotEmpty();
        RuleFor(x => x.KycFileId).NotEmpty();

        // Shape is NOT validated here. The store issues these references and is the only thing
        // that knows their format; re-stating it would make a change to the storage layout a
        // two-file change, and the second file is the one everybody forgets. An unknown or
        // malformed ref comes back as KYC_VERIFICATION_IMAGE_NOT_FOUND from the handler.
        RuleFor(x => x.DocumentStorageRef).NotEmpty();
        RuleFor(x => x.SelfieStorageRef).NotEmpty();

        // A value outside the enum would reach the service as an unknown doc_type and come back as
        // a 422 that reads like a bad photograph.
        RuleFor(x => x.DocumentType).IsInEnum();

        // The replay counts on this one, and an Attempt of 0 would reset the retry budget every
        // round — an outage would then queue itself forever.
        RuleFor(x => x.Attempt).GreaterThan(0);

        // Not RequestedBy: the replay job carries the ORIGINAL requester and must not be refused
        // when that agent's id is empty because the file was opened by an automatic trigger.
    }
}
