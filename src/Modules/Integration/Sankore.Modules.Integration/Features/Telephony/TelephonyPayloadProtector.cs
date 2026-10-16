namespace Sankore.Modules.Integration.Features.Telephony;

using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Sankore.Shared.Infrastructure.Crypto;

/// <summary>
/// Encrypts a provider's webhook body before it is stored (TEL-B-07).
///
/// <para>
/// <b>Why the body is encrypted at all.</b> A telephony event carries BOTH numbers of a call in
/// clear — the customer's and the agent's — so <c>tel_webhook_event</c> is the one table of this
/// family that would otherwise hold a readable telephone number, in volume, for every call the
/// institution makes. It is also the table an operator opens when a call did not land, which is
/// exactly the shoulder-surfing case field encryption exists for.
/// </para>
///
/// <para>
/// <b>Base64 of the RAW bytes, not the decoded text.</b> A body that is not valid UTF-8 — and a
/// provider is free to send one — does not survive a decode and re-encode, so storing text would
/// quietly corrupt the one copy an operator has to diagnose from. Base64 round-trips exactly.
/// </para>
///
/// <para>
/// No blind index, deliberately. Nothing searches for an event by its content, and an index would
/// make "which calls mention this number" a query over a table nobody audits.
/// </para>
/// </summary>
internal sealed class TelephonyPayloadProtector(
    [FromKeyedServices(Infrastructure.Crypto.IntegrationFieldProtection.Key)] IFieldEncryptor encryptor)
{
    internal byte[] Protect(ReadOnlySpan<byte> body)
    {
        var envelope = encryptor.Encrypt(Convert.ToBase64String(body))
            ?? throw new InvalidOperationException(
                "The integration field encryptor returned nothing for a webhook body.");

        return Encoding.UTF8.GetBytes(envelope);
    }

    /// <summary>
    /// The body back. Null on a payload that will not open: a key problem must not stop the job
    /// that is journalling the call, which re-reads the provider anyway and needs nothing from
    /// here.
    /// </summary>
    internal byte[]? Reveal(byte[]? stored)
    {
        if (stored is not { Length: > 0 }) return null;

        try
        {
            var base64 = encryptor.Decrypt(Encoding.UTF8.GetString(stored));

            return base64 is null ? null : Convert.FromBase64String(base64);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
            return null;
        }
    }
}
