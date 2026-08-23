namespace Sankore.Modules.Administration.Tests.SharedInfrastructure;

using FluentAssertions;
using MediatR;
using NSubstitute;
using Sankore.Shared.Infrastructure.Auth;
using Sankore.Shared.Infrastructure.Behaviors;
using Sankore.Shared.Kernel;
using Xunit;
using ITenantCtx = Sankore.Shared.Kernel.ITenantContext;

public sealed class AuditBehaviorTests
{
    // ── fixtures ────────────────────────────────────────────────────────────

    // Plain command — no resource
    private sealed record PlainCommand(string Data) : IRequest<Result>, ICommand;

    // Command with IResourceCommand — known ID
    private sealed record UpdateResourceCommand(Guid ResourceEntityId, string Data)
        : IRequest<Result>, ICommand, IResourceCommand
    {
        public string ResourceType => "Widget";
        public string? ResourceId => ResourceEntityId.ToString();
    }

    // Command with IResourceCommand — ID not yet known (create scenario)
    private sealed record CreateResourceCommand(string Name)
        : IRequest<Result>, ICommand, IResourceCommand
    {
        public string ResourceType => "Widget";
        public string? ResourceId => null;
    }

    // Non-command (query) — must NOT be audited
    private sealed record ReadQuery(Guid Id) : IRequest<Result>;

    // ── helpers ─────────────────────────────────────────────────────────────

    private static (AuditBehavior<TReq, TResp> behavior, IAuditWriter writer)
        BuildBehavior<TReq, TResp>()
        where TReq : notnull
    {
        var writer = Substitute.For<IAuditWriter>();
        writer.WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>())
              .Returns(Task.CompletedTask);

        var currentUser = Substitute.For<ICurrentUser>();
        var tenantId = Guid.NewGuid();
        currentUser.Id.Returns(Guid.NewGuid());
        currentUser.TenantId.Returns(tenantId);

        var tenant = Substitute.For<ITenantCtx>();
        tenant.CurrentTenantId.Returns(tenantId);

        return (new AuditBehavior<TReq, TResp>(writer, currentUser, tenant), writer);
    }

    private static RequestHandlerDelegate<Result> SuccessDelegate()
        => () => Task.FromResult(Result.Ok());

    private static RequestHandlerDelegate<Result> FailureDelegate()
        => () => Task.FromResult(Result.Fail("something broke"));

    // ── S1 : commande auditée → WriteAsync appelé une fois ───────────────

    [Fact]
    public async Task Audits_a_command_once()
    {
        var (behavior, writer) = BuildBehavior<PlainCommand, Result>();

        await behavior.Handle(new PlainCommand("x"), SuccessDelegate(), CancellationToken.None);

        await writer.Received(1).WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>());
    }

    // ── S2 : query non-auditée → WriteAsync jamais appelé ────────────────

    [Fact]
    public async Task Does_not_audit_a_query()
    {
        var (behavior, writer) = BuildBehavior<ReadQuery, Result>();

        await behavior.Handle(new ReadQuery(Guid.NewGuid()),
            () => Task.FromResult(Result.Ok()), CancellationToken.None);

        await writer.DidNotReceive().WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>());
    }

    // ── S3 : outcome SUCCESS sur Result.Ok() ─────────────────────────────

    [Fact]
    public async Task Records_SUCCESS_outcome_on_ok_result()
    {
        var (behavior, writer) = BuildBehavior<PlainCommand, Result>();
        AuditEntry? captured = null;
        writer.WriteAsync(Arg.Do<AuditEntry>(e => captured = e), Arg.Any<CancellationToken>())
              .Returns(Task.CompletedTask);

        await behavior.Handle(new PlainCommand("ok"), SuccessDelegate(), CancellationToken.None);

        captured!.Outcome.Should().Be("SUCCESS");
        captured.ErrorDetail.Should().BeNull();
    }

    // ── S4 : outcome FAILURE sur Result.Fail() ───────────────────────────

    [Fact]
    public async Task Records_FAILURE_outcome_on_fail_result()
    {
        var (behavior, writer) = BuildBehavior<PlainCommand, Result>();
        AuditEntry? captured = null;
        writer.WriteAsync(Arg.Do<AuditEntry>(e => captured = e), Arg.Any<CancellationToken>())
              .Returns(Task.CompletedTask);

        await behavior.Handle(new PlainCommand("fail"), FailureDelegate(), CancellationToken.None);

        captured!.Outcome.Should().Be("FAILURE");
        captured.ErrorDetail.Should().Be("something broke");
    }

    // ── S5 : Action = nom du type de la commande ─────────────────────────

    [Fact]
    public async Task Action_is_command_type_name()
    {
        var (behavior, writer) = BuildBehavior<PlainCommand, Result>();
        AuditEntry? captured = null;
        writer.WriteAsync(Arg.Do<AuditEntry>(e => captured = e), Arg.Any<CancellationToken>())
              .Returns(Task.CompletedTask);

        await behavior.Handle(new PlainCommand("x"), SuccessDelegate(), CancellationToken.None);

        captured!.Action.Should().Be(nameof(PlainCommand));
    }

    // ── S6 : IResourceCommand → ResourceType + ResourceId peuplés ────────

    [Fact]
    public async Task Populates_ResourceType_and_ResourceId_from_IResourceCommand()
    {
        var (behavior, writer) = BuildBehavior<UpdateResourceCommand, Result>();
        AuditEntry? captured = null;
        writer.WriteAsync(Arg.Do<AuditEntry>(e => captured = e), Arg.Any<CancellationToken>())
              .Returns(Task.CompletedTask);

        var entityId = Guid.NewGuid();
        await behavior.Handle(
            new UpdateResourceCommand(entityId, "data"),
            SuccessDelegate(),
            CancellationToken.None);

        captured!.ResourceType.Should().Be("Widget");
        captured.ResourceId.Should().Be(entityId.ToString());
    }

    // ── S7 : create command → ResourceId null, ResourceType peuplé ───────

    [Fact]
    public async Task ResourceId_is_null_for_create_commands()
    {
        var (behavior, writer) = BuildBehavior<CreateResourceCommand, Result>();
        AuditEntry? captured = null;
        writer.WriteAsync(Arg.Do<AuditEntry>(e => captured = e), Arg.Any<CancellationToken>())
              .Returns(Task.CompletedTask);

        await behavior.Handle(new CreateResourceCommand("new thing"), SuccessDelegate(), CancellationToken.None);

        captured!.ResourceType.Should().Be("Widget");
        captured.ResourceId.Should().BeNull();
    }

    // ── S8 : commande sans IResourceCommand → nulls ───────────────────────

    [Fact]
    public async Task ResourceType_and_ResourceId_are_null_for_plain_commands()
    {
        var (behavior, writer) = BuildBehavior<PlainCommand, Result>();
        AuditEntry? captured = null;
        writer.WriteAsync(Arg.Do<AuditEntry>(e => captured = e), Arg.Any<CancellationToken>())
              .Returns(Task.CompletedTask);

        await behavior.Handle(new PlainCommand("data"), SuccessDelegate(), CancellationToken.None);

        captured!.ResourceType.Should().BeNull();
        captured.ResourceId.Should().BeNull();
    }

    // ── S9 : PayloadJson ne contient pas les valeurs sensibles ────────────

    [Fact]
    public async Task PayloadJson_does_not_contain_sensitive_values()
    {
        var (behavior, writer) = BuildBehavior<
            Sankore.Modules.Administration.Features.Authentication.Login.LoginCommand,
            Result<Sankore.Modules.Administration.Features.Authentication.Login.LoginResult>>();

        AuditEntry? captured = null;
        writer.WriteAsync(Arg.Do<AuditEntry>(e => captured = e), Arg.Any<CancellationToken>())
              .Returns(Task.CompletedTask);

        var cmd = new Sankore.Modules.Administration.Features.Authentication.Login.LoginCommand(
            "admin@sankore.sn", "Top$ecret99!");

        await behavior.Handle(
            cmd,
            () => Task.FromResult(Result.Fail<Sankore.Modules.Administration.Features.Authentication.Login.LoginResult>("wrong password")),
            CancellationToken.None);

        captured!.PayloadJson.Should().Contain("***");
        captured.PayloadJson.Should().NotContain("Top$ecret99!");
    }

    // ── S10 : UserId + TenantId issus de ICurrentUser ─────────────────────

    [Fact]
    public async Task UserId_and_TenantId_come_from_ICurrentUser()
    {
        var writer = Substitute.For<IAuditWriter>();
        writer.WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var currentUser = Substitute.For<ICurrentUser>();
        currentUser.Id.Returns(userId);
        currentUser.TenantId.Returns(tenantId);

        var tenant = Substitute.For<ITenantCtx>();
        tenant.CurrentTenantId.Returns(tenantId);
        var behavior = new AuditBehavior<PlainCommand, Result>(writer, currentUser, tenant);
        AuditEntry? captured = null;
        writer.WriteAsync(Arg.Do<AuditEntry>(e => captured = e), Arg.Any<CancellationToken>())
              .Returns(Task.CompletedTask);

        await behavior.Handle(new PlainCommand("x"), SuccessDelegate(), CancellationToken.None);

        captured!.UserId.Should().Be(userId);
        captured.TenantId.Should().Be(tenantId);
    }
}
