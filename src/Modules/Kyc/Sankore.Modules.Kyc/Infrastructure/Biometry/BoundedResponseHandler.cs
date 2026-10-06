namespace Sankore.Modules.Kyc.Infrastructure.Biometry;

using Microsoft.Extensions.Options;

/// <summary>
/// Refuses a biometric answer larger than <see cref="BiometryOptions.MaxResponseBytes"/> before it
/// is buffered.
///
/// <para>
/// It exists because the generated client reads the whole body into a string — it has no size
/// guard, and the hand-written client's bounded read disappeared with it. The cap is not about the
/// service: a biometric answer is a few kilobytes of JSON. It is about a misrouted URL answering
/// with a login page, a proxy error or an image, which would otherwise be buffered into the
/// request's memory and then handed to a JSON parser.
/// </para>
///
/// <para>
/// A dedicated exception rather than <c>HttpClient.MaxResponseContentBufferSize</c>, which reports
/// the overflow as an <see cref="HttpRequestException"/> indistinguishable from the service being
/// unreachable — the distinct <c>BIOMETRY_RESPONSE_TOO_LARGE</c> code is what tells an operator to
/// look at the URL rather than at the service.
/// </para>
/// </summary>
internal sealed class BoundedResponseHandler(IOptions<BiometryOptions> options) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        var max = options.Value.MaxResponseBytes;

        // The declared length is honoured first: it costs nothing and avoids reading at all.
        if (response.Content.Headers.ContentLength > max)
        {
            response.Dispose();
            throw new BiometryResponseTooLargeException(max);
        }

        // No declared length (chunked): buffer with one byte of headroom, which is all it takes to
        // know we are over the cap.
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        using var buffered = new MemoryStream();
        var buffer = new byte[8192];

        while (true)
        {
            var room = max + 1 - buffered.Length;
            if (room <= 0)
            {
                response.Dispose();
                throw new BiometryResponseTooLargeException(max);
            }

            var read = await stream.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, room)), cancellationToken);

            if (read == 0)
                break;

            buffered.Write(buffer, 0, read);

            if (buffered.Length > max)
            {
                response.Dispose();
                throw new BiometryResponseTooLargeException(max);
            }
        }

        // Replaced with the bytes already read: the original stream is consumed, and the generated
        // client is about to read the content itself.
        var replacement = new ByteArrayContent(buffered.ToArray());

        foreach (var header in response.Content.Headers)
            replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);

        response.Content = replacement;
        return response;
    }
}

/// <summary>Thrown by <see cref="BoundedResponseHandler"/>; caught by <see cref="HttpBiometryClient"/>.</summary>
internal sealed class BiometryResponseTooLargeException(long maxBytes)
    : Exception($"The biometric service answered more than {maxBytes} bytes.")
{
    public long MaxBytes { get; } = maxBytes;
}
