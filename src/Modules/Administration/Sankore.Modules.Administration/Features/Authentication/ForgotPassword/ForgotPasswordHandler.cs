using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Sankore.Modules.Administration.Domain;
using Sankore.Modules.Administration.Infrastructure;
using Sankore.Modules.Notifications.PublicApi;
using Sankore.Shared.Kernel;

namespace Sankore.Modules.Administration.Features.Authentication.ForgotPassword;

internal sealed class ForgotPasswordHandler(
    AdministrationDbContext db,
    UserManager<AppUser> userManager,
    ITenantContext tenantContext,
    INotificationsModule notifications
) : IRequestHandler<ForgotPasswordCommand, Result<ForgotPasswordResult>>
{
    // Always return the same message — never reveal whether the email is registered.
    private const string GenericMessage =
        "If an account with that email exists, a password reset link has been sent.";

    public async Task<Result<ForgotPasswordResult>> Handle(
        ForgotPasswordCommand request, CancellationToken ct)
    {
        var tenantId = tenantContext.CurrentTenantId;
        var normalizedEmail = request.Email.ToUpperInvariant();

        var user = await db.Users
            .IgnoreQueryFilters()
            .Where(u => u.TenantId == tenantId && u.NormalizedEmail == normalizedEmail)
            .FirstOrDefaultAsync(ct);

        // Silent success for unknown email or inactive account
        if (user is null || user.Status != UserStatus.Active)
            return Result.Ok(new ForgotPasswordResult(GenericMessage));

        var resetToken = await userManager.GeneratePasswordResetTokenAsync(user);

        // Resolve locale from user profile; fall back to "fr"
        var locale = await db.UserProfiles
            .IgnoreQueryFilters()
            .Where(p => p.UserId == user.Id)
            .Select(p => p.DefaultLanguage)
            .FirstOrDefaultAsync(ct) ?? "fr";
        
        // Idempotency: one reset email per user per calendar day (prevents spam)
        var idempotencyKey = $"password-reset-{user.Id}-{DateTimeOffset.UtcNow:yyyyMMdd}";

        await notifications.QueueEmailAsync(new QueueEmailRequest(
            TemplateKey:    "user.password-forgot",
            RecipientEmail: user.Email!,
            RecipientName:  user.FullName,
            Module:         "Administration",
            Locale:         locale,
            TemplateData: new Dictionary<string, object>
            {
                ["full_name"]   = user.FullName,
                ["reset_token"] = resetToken,
                ["tenant_id"]   = tenantId.ToString(),
                ["user_id"]     = user.Id.ToString()
            },
            IdempotencyKey: idempotencyKey,
            TenantId:       tenantId), ct);

        return Result.Ok(new ForgotPasswordResult(GenericMessage));
    }
}