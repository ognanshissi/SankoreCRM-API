using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Users.MyProfile;

/// <summary>
/// L'utilisateur corrige son propre profil. Ni l'e-mail ni le matricule, ni l'agence ni le
/// superviseur : ceux-là restent à l'administrateur, par <c>PUT users/{id}</c>.
/// </summary>
public sealed record UpdateMyProfileCommand(UpdateMyProfileRequest Request) : IRequest<Result>, ICommand;

internal sealed class UpdateMyProfileValidator : AbstractValidator<UpdateMyProfileCommand>
{
    private static readonly string[] InterfaceLanguages = ["fr", "en"];

    public UpdateMyProfileValidator()
    {
        RuleFor(x => x.Request.FirstName).NotEmpty().MaximumLength(200).WithName("Prénom");
        RuleFor(x => x.Request.LastName).NotEmpty().MaximumLength(200).WithName("Nom");
        RuleFor(x => x.Request.WorkPhone).MaximumLength(30).Matches(@"^\+?[0-9 ().-]*$")
            .WithMessage("Le téléphone professionnel n'est pas un numéro valide.");
        RuleFor(x => x.Request.WhatsAppNumber).MaximumLength(30).Matches(@"^\+?[0-9 ().-]*$")
            .WithMessage("Le numéro WhatsApp n'est pas un numéro valide.");
        RuleFor(x => x.Request.Signature).MaximumLength(UserProfile.MaxSignatureLength);
        RuleFor(x => x.Request.InterfaceLanguage)
            .Must(l => InterfaceLanguages.Contains(LanguageCode.NormalizeOrNull(l)))
            .WithMessage("Langue d'interface inconnue : fr ou en.");
        RuleFor(x => x.Request.AmountFormat)
            .Must(f => Enum.TryParse<AmountFormat>(f, ignoreCase: true, out _))
            .WithMessage("Format des montants inconnu.");
        RuleFor(x => x.Request.TimeZone).NotEmpty().MaximumLength(64);
    }
}

internal sealed class UpdateMyProfileHandler(AdministrationDbContext db, ICurrentUser currentUser)
    : IRequestHandler<UpdateMyProfileCommand, Result>
{
    public async Task<Result> Handle(UpdateMyProfileCommand command, CancellationToken ct)
    {
        var req = command.Request;
        var user = await db.Users.AsTracking().FirstOrDefaultAsync(u => u.Id == currentUser.Id, ct);
        if (user is null)
            return Result.Fail("USER_NOT_FOUND");

        var profile = await db.UserProfiles.AsTracking().FirstOrDefaultAsync(p => p.UserId == user.Id, ct);
        var isNew = profile is null;
        profile ??= UserProfile.Create(user.TenantId, user.Id);

        try
        {
            user.RenameSelf(req.FirstName, req.LastName);
            profile.UpdateBySelf(
                req.WorkPhone, req.WhatsAppNumber, req.Signature, req.InterfaceLanguage, req.TimeZone,
                Enum.Parse<AmountFormat>(req.AmountFormat, ignoreCase: true));
            // Les deux portent la langue : PreferredLanguage pour les e-mails et le jeton,
            // DefaultLanguage pour GET users/me. Les laisser diverger donnerait deux réponses.
            user.SetPreferredLanguage(req.InterfaceLanguage);
        }
        catch (DomainException ex)
        {
            return Result.Fail(ex.Message);
        }

        if (isNew) db.UserProfiles.Add(profile);
        await db.SaveChangesAsync(ct);
        return Result.Ok();
    }
}

internal static class UpdateMyProfileEndpoint
{
    internal static IEndpointRouteBuilder MapUpdateMyProfile(this IEndpointRouteBuilder app)
    {
        app.MapPut("me/profile", async (UpdateMyProfileRequest req, ISender sender, CancellationToken ct) =>
            {
                var result = await sender.Send(new UpdateMyProfileCommand(req), ct);
                return result.IsSuccess
                    ? Results.NoContent()
                    : Results.Problem(result.Error, statusCode: StatusCodes.Status400BadRequest);
            })
            .WithName("UpdateMyProfile")
            .WithSummary("Enregistre le profil de l'utilisateur connecté (nom, téléphones, signature, langue, fuseau, format des montants).")
            .WithTags("Users")
            .RequireAuthorization()
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest);

        return app;
    }
}
