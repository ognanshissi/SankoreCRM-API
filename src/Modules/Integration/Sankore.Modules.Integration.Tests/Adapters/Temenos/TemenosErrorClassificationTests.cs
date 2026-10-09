namespace Sankore.Modules.Integration.Tests.Adapters.Temenos;

using System.Net;
using FluentAssertions;
using Sankore.Modules.Integration.PublicApi;
using Xunit;

/// <summary>
/// Transient, functional or technical — for every answer Transact can give (INT-12, criterion 4).
///
/// <para>
/// The most consequential behaviour in the adapter, because the family is not cosmetic:
/// <c>Transient</c> makes the dispatcher try again, <c>Functional</c> parks the command in a queue
/// a human empties, <c>Technical</c> wakes an administrator and never retries. Wrong in one
/// direction, the platform hammers a system that refused on the merits; wrong in the other, a
/// customer's account is never opened because of a thirty-second outage, with a rejection row
/// that reads like a refusal by the bank.
/// </para>
///
/// <para>
/// Driven THROUGH the adapter and not against the classifier directly, wherever that is possible:
/// what has to hold is that a given answer on the wire produces a given family at the port, and a
/// unit test of the classifier would still pass if the transport stopped calling it. The two facts
/// that cannot be reached through a status code — a socket that never opens, a body that is not
/// JSON — are exercised through the stub's own modes.
/// </para>
/// </summary>
public sealed class TemenosErrorClassificationTests
{
    /// <summary>
    /// Availability. Every one of these says "we learned nothing", so the command must come back.
    /// </summary>
    [Theory]
    [InlineData(408, IntegrationErrors.Timeout)]
    [InlineData(429, IntegrationErrors.RateLimited)]
    [InlineData(502, IntegrationErrors.Unavailable)]
    [InlineData(503, IntegrationErrors.Unavailable)]
    [InlineData(504, IntegrationErrors.Unavailable)]
    public async Task An_availability_status_is_transient(int status, string code)
    {
        var result = await Call(status);

        result.Family.Should().Be(ErrorFamily.Transient);
        result.Code.Should().Be(code);
        result.IsRetryable.Should().BeTrue();
    }

    /// <summary>
    /// A 500 is the installation's own bug or its own overload — not a statement about our
    /// request. Transient, because parking the command would make a human re-enter a write that
    /// the next attempt would have carried, and the command's attempt budget stops the loop.
    /// </summary>
    [Theory]
    [InlineData(500)]
    [InlineData(599)]
    public async Task Any_other_server_error_is_transient_as_well(int status)
    {
        var result = await Call(status);

        result.Family.Should().Be(ErrorFamily.Transient);
        result.Code.Should().Be(IntegrationErrors.Unavailable);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task A_refused_authentication_is_technical_and_never_retried(int status)
    {
        // The transport has already renewed the token once by the time this classification is
        // reached, so a 401 that survives it is about the credentials and not about the clock.
        var result = await Call(status);

        result.Family.Should().Be(ErrorFamily.Technical);
        result.Code.Should().Be(IntegrationErrors.AuthenticationRefused);
        result.IsRetryable.Should().BeFalse();
    }

    [Fact]
    public async Task A_404_on_a_referenced_entity_is_functional_not_found()
    {
        var result = await Call(404, TemenosFixtures.RecordNotFoundError);

        // An answer, not an outage: retrying a lookup of something absent changes nothing, and a
        // human has to decide what the dangling reference means.
        result.Family.Should().Be(ErrorFamily.Functional);
        result.Code.Should().Be(IntegrationErrors.ExternalEntityNotFound);
    }

    [Fact]
    public async Task A_409_keeps_its_HTTP_meaning_and_is_a_functional_duplicate()
    {
        var result = await Call(409, TemenosFixtures.DuplicateError);

        result.Family.Should().Be(ErrorFamily.Functional);
        result.Code.Should().Be(IntegrationErrors.Duplicate);
        result.IsRetryable.Should().BeFalse("the record exists, so another attempt refuses again");
    }

    [Fact]
    public async Task A_refused_field_value_on_a_400_is_functional()
    {
        var result = await Call(400, TemenosFixtures.RejectedFieldError);

        // The envelope names a field: the installation ran, read our request, and said no to a
        // value. A clerk can correct the CRM record and the command can be replayed.
        result.Family.Should().Be(ErrorFamily.Functional);
        result.Code.Should().Be(IntegrationErrors.Rejected);
    }

    [Fact]
    public async Task A_malformed_request_on_a_400_is_technical()
    {
        var result = await Call(400, TemenosFixtures.MalformedRequestError);

        // No field named and the type says it is not a business matter: ours to fix, and a clerk
        // can do nothing with it.
        result.Family.Should().Be(ErrorFamily.Technical);
        result.Code.Should().Be(IntegrationErrors.PayloadInvalid);
    }

    [Fact]
    public async Task A_400_with_no_readable_envelope_leans_technical()
    {
        var result = await Call(400, string.Empty);

        // A refusal we cannot read is likelier to be a wire-format mismatch of ours — see the
        // banner in TemenosWire.cs — than a considered business decision, and Technical is the
        // family that puts it in front of whoever can compare the payload with the document.
        result.Family.Should().Be(ErrorFamily.Technical);
        result.Code.Should().Be(IntegrationErrors.PayloadInvalid);
    }

    [Fact]
    public async Task A_duplicate_announced_on_a_422_is_still_a_duplicate()
    {
        var result = await Call(422, TemenosFixtures.DuplicateError);

        result.Family.Should().Be(ErrorFamily.Functional);
        result.Code.Should().Be(IntegrationErrors.Duplicate);
    }

    [Fact]
    public async Task A_record_not_found_announced_on_a_400_is_read_as_absent_and_not_as_invalid()
    {
        var result = await Call(400, TemenosFixtures.RecordNotFoundError);

        // T24 is fond of answering 400 with "RECORD NOT FOUND", and reading it as a malformed
        // payload would wake an administrator over a reference a clerk mistyped.
        result.Family.Should().Be(ErrorFamily.Functional);
        result.Code.Should().Be(IntegrationErrors.ExternalEntityNotFound);
    }

    [Fact]
    public async Task A_maintenance_window_stays_transient_even_when_its_message_mentions_not_found()
    {
        var result = await Call(503, TemenosFixtures.MaintenanceError);

        // The status wins over the message. A close-of-business window reported as "RECORD NOT
        // FOUND" would otherwise park every command of the night in the rejection queue.
        result.Family.Should().Be(ErrorFamily.Transient);
        result.Code.Should().Be(IntegrationErrors.Unavailable);
    }

    [Theory]
    [InlineData(405)]
    [InlineData(415)]
    public async Task Any_other_client_error_is_technical(int status)
    {
        // We are speaking to the endpoint wrongly, which is a deployment or a code fault and
        // never something a retry or a clerk can fix.
        var result = await Call(status);

        result.Family.Should().Be(ErrorFamily.Technical);
        result.Code.Should().Be(IntegrationErrors.PayloadInvalid);
    }

    [Fact]
    public async Task An_error_envelope_carried_on_an_HTTP_200_is_still_a_refusal()
    {
        var result = await Call(200, TemenosFixtures.ErrorOnHttp200);

        // IRIS does this. A transport that read the envelope as a success because the status line
        // was 200 would report a duplicate customer as created.
        result.IsFailure.Should().BeTrue();
        result.Family.Should().Be(ErrorFamily.Functional);
        result.Code.Should().Be(IntegrationErrors.Duplicate);
    }

    [Fact]
    public async Task A_body_that_is_not_JSON_is_a_functional_unexpected_response()
    {
        var result = await Call(200, TemenosFixtures.GatewayHtml);

        // A gateway's HTML page on a 200. A refusal to guess, and the detail points at the one
        // file to compare with the installation's document.
        result.Family.Should().Be(ErrorFamily.Functional);
        result.Code.Should().Be(IntegrationErrors.UnexpectedResponse);
        result.Detail.Should().Contain("TemenosWire.cs");
    }

    [Fact]
    public async Task A_socket_that_never_opens_is_transient()
    {
        using var harness = TemenosTestHarness.Create();
        harness.Transport.Mode = StubMode.Unreachable;

        var result = await harness.Adapter.GetBalanceAsync(
            new ExternalId(TemenosFixtures.SeededAccountId), CancellationToken.None);

        // We never reached the installation, so we learned nothing about the request — and the
        // pre-create search plus the idempotency key are what make trying again safe.
        result.Family.Should().Be(ErrorFamily.Transient);
        result.Code.Should().Be(IntegrationErrors.Unavailable);
    }

    [Fact]
    public async Task No_answer_within_the_budget_is_transient()
    {
        using var harness = TemenosTestHarness.Create();
        harness.Transport.Mode = StubMode.TimingOut;

        var result = await harness.Adapter.GetBalanceAsync(
            new ExternalId(TemenosFixtures.SeededAccountId), CancellationToken.None);

        result.Family.Should().Be(ErrorFamily.Transient);
        result.Code.Should().Be(IntegrationErrors.Timeout);
    }

    [Fact]
    public async Task The_caller_cancelling_is_rethrown_and_not_reported_as_an_answer()
    {
        using var harness = TemenosTestHarness.Create();
        harness.Transport.Mode = StubMode.TimingOut;

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        // The one exception that leaves an adapter. A caller giving up is not a statement by the
        // installation, and swallowing it as a transient failure would have the dispatcher retry
        // work that a shutdown has just abandoned.
        var act = () => harness.Adapter.GetBalanceAsync(
            new ExternalId(TemenosFixtures.SeededAccountId), cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task A_detail_never_quotes_the_installation_s_message()
    {
        var result = await Call(409, TemenosFixtures.DuplicateError);

        // INT-08's second criterion. A T24 refusal reads "CUSTOMER KOUASSI/0708... ALREADY
        // EXISTS": it quotes the rejected value, which is the customer's name and phone number,
        // and the detail is logged and shown to operators. The stable code is kept; the prose is
        // dropped.
        result.Detail.Should().Contain("T24.CUSTOMER.0012");
        result.Detail.Should().NotContain("ALREADY EXISTS");
    }

    [Fact]
    public async Task A_detail_names_the_operation_so_an_administrator_knows_where_to_look()
    {
        var result = await Call(500);

        result.Detail.Should().Contain("ReadBalance");
    }

    /// <summary>
    /// Forces one answer onto the installation and returns what the port made of it. The balance
    /// read is the cheapest call that goes straight to the wire with no mapping and no search in
    /// front of it.
    /// </summary>
    private static async Task<IntegrationResult> Call(int status, string body = "")
    {
        using var harness = TemenosTestHarness.Create();

        harness.Transport.ForcedApiStatus = (HttpStatusCode)status;
        harness.Transport.ForcedApiBody = body;

        return await harness.Adapter.GetBalanceAsync(
            new ExternalId(TemenosFixtures.SeededAccountId), CancellationToken.None);
    }
}
