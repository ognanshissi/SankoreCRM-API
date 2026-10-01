namespace Sankore.Shared.Infrastructure.Tests.ObjectStorage;

using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Shared.ObjectStorage;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// <see cref="R2ObjectBackend"/> against a real Cloudflare R2 bucket.
///
/// <para>
/// Opt-in, and silent when the four environment variables below are unset, so a normal
/// <c>dotnet test</c> neither needs credentials nor reaches the network:
/// </para>
/// <code>
/// export SANKORE_R2_ACCOUNT_ID=...          # Cloudflare account id
/// export SANKORE_R2_ACCESS_KEY_ID=...       # R2 API token access key id
/// export SANKORE_R2_SECRET_ACCESS_KEY=...   # R2 API token secret
/// export SANKORE_R2_BUCKET=sankore-objects-test
/// dotnet test src/Shared/Sankore.Shared.Infrastructure.Tests \
///   --filter "FullyQualifiedName~R2ObjectBackendIntegrationTests"
/// </code>
///
/// <para>
/// It is not decoration. Three of the decisions in <see cref="R2ObjectBackend"/> are claims about
/// a service nothing in this repository runs, and a test double written from the same assumptions
/// cannot contradict them:
/// </para>
/// <list type="number">
/// <item>that <c>RequestChecksumCalculation.WHEN_REQUIRED</c> is what R2 needs — the SDK's own
/// default puts an <c>x-amz-checksum-crc32</c> header on every upload, and when R2 rejects it the
/// error names a signature, not a checksum;</item>
/// <item>that R2 honours <c>If-None-Match: *</c>, which is the only thing standing between KYC
/// evidence and being silently replaced in place — a provider that ignored the header would
/// overwrite, and every other test here would still pass;</item>
/// <item>that <c>ForcePathStyle</c> plus <c>AuthenticationRegion = "auto"</c> address the bucket
/// at all.</item>
/// </list>
///
/// <para>
/// Xunit's <c>SkippableFact</c> is not referenced in this project (checked), so an unconfigured
/// run returns early and says why through the test output rather than reporting a skip.
/// </para>
///
/// <para>
/// Every key it writes sits under a one-run prefix and is deleted in a <c>finally</c>: the bucket
/// is shared with whoever else runs this, and leftover objects would make the pagination
/// assertions depend on previous runs.
/// </para>
/// </summary>
public sealed class R2ObjectBackendIntegrationTests(ITestOutputHelper output)
{
    private const long Unbounded = long.MaxValue;

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public async Task The_full_contract_should_hold_against_a_real_bucket()
    {
        if (!TryReadEnvironment(out var options, out var bucket, out var missing))
        {
            output.WriteLine(
                $"Skipped: {missing} is not set. This test is the only thing that checks the "
                + "checksum and conditional-write assumptions against R2 — see the class comment "
                + "for how to run it.");
            return;
        }

        using var sut = new R2ObjectBackend(options, bucket, NullLogger<R2ObjectBackend>.Instance);

        // One prefix per run: the bucket is shared, and a leftover object from a crashed run
        // would otherwise be counted by the listing assertions below.
        var prefix = $"integration-tests/{DateTime.UtcNow:yyyyMMddTHHmmss}-{Guid.NewGuid():N}";
        var written = new List<string>();

        async Task<string> Put(string suffix, byte[] content)
        {
            var key = $"{prefix}/{suffix}";
            await sut.PutAsync(key, content);
            written.Add(key);
            return key;
        }

        try
        {
            // --- put / get round trip, on bytes that are not text -------------------------------
            // KYC evidence reaches a backend as AES-GCM ciphertext, so random bytes are the
            // realistic payload: a transfer encoding that mangled them would pass a UTF-8 probe.
            var payload = RandomNumberGenerator.GetBytes(3 * 1024);
            var roundTrip = await Put("round-trip.bin", payload);

            (await sut.GetAsync(roundTrip, Unbounded)).Should().Equal(payload,
                "this is also the assertion that the checksum configuration is right — a rejected "
                + "upload throws before reaching here");

            // --- absence is never an error -----------------------------------------------------
            var absent = $"{prefix}/never-written.bin";
            (await sut.GetAsync(absent, Unbounded)).Should().BeNull();
            (await sut.DeleteAsync(absent)).Should().BeFalse(
                "S3 deletes answer 204 whether or not the key existed; the backend must still "
                + "report what it removed");

            // --- the ceiling, inclusive, decided from Content-Length ---------------------------
            var big = await Put("big.bin", new byte[4096]);
            (await sut.GetAsync(big, 1024)).Should().BeNull();
            (await sut.GetAsync(big, 4096)).Should().HaveCount(4096, "the limit is inclusive");

            // --- THE collision test ------------------------------------------------------------
            // If R2 ignored If-None-Match this would overwrite and the Get below would return
            // "second". Nothing else in the suite can tell the difference.
            var once = await Put("once.bin", Bytes("first"));
            var overwrite = () => sut.PutAsync(once, Bytes("second"));

            await overwrite.Should().ThrowAsync<InvalidOperationException>(
                "evidence a decision was taken on must never be replaced in place");
            (await sut.GetAsync(once, Unbounded)).Should().Equal(Bytes("first"));

            // --- malformed keys: absent on read, refused on write ------------------------------
            foreach (var bad in new[] { "", "   ", "/etc/passwd", "../../etc/passwd", "a/../../b" })
            {
                (await sut.GetAsync(bad, Unbounded)).Should().BeNull($"'{bad}' is not a key");
                (await sut.DeleteAsync(bad)).Should().BeFalse($"'{bad}' is not a key");

                var write = () => sut.PutAsync(bad, Bytes("x"));
                await write.Should().ThrowAsync<ArgumentException>();
            }

            // --- listing: prefix-filtered, paginated, '/'-separated ----------------------------
            // Enough keys to need more than one ListObjectsV2 page is 1000+, which is not worth
            // the upload time; the continuation loop is pinned over the fake instead. What only a
            // real bucket shows is that the prefix filters server-side and the keys come back
            // exactly as they were written.
            for (var i = 0; i < 3; i++) await Put($"listed/{i}.bin", Bytes($"{i}"));

            var listed = await Collect(sut.ListAsync($"{prefix}/listed/"));

            listed.Should().BeEquivalentTo(
                [$"{prefix}/listed/0.bin", $"{prefix}/listed/1.bin", $"{prefix}/listed/2.bin"]);
            listed.Should().OnlyContain(k => !k.Contains('\\'), "keys are '/'-separated everywhere");

            // The migration feeds every listed key straight back to GetAsync.
            foreach (var key in listed) (await sut.GetAsync(key, Unbounded)).Should().NotBeNull();

            // --- delete reports what it removed, then stays idempotent -------------------------
            (await sut.DeleteAsync(once)).Should().BeTrue();
            written.Remove(once);
            (await sut.GetAsync(once, Unbounded)).Should().BeNull();
            (await sut.DeleteAsync(once)).Should().BeFalse();

            output.WriteLine($"Ran against bucket '{bucket}' under prefix '{prefix}'.");
        }
        finally
        {
            // Listed rather than only replayed from `written`: a failure partway through may have
            // left an object this test never recorded, and the next run's counts would include it.
            var strays = await Collect(sut.ListAsync(prefix));

            foreach (var key in written.Concat(strays).Distinct(StringComparer.Ordinal))
            {
                try
                {
                    await sut.DeleteAsync(key);
                }
                catch (Exception ex)
                {
                    // Never mask the real failure with a cleanup failure.
                    output.WriteLine($"Could not clean up '{key}': {ex.Message}");
                }
            }
        }
    }

    private static bool TryReadEnvironment(
        out ObjectStorageOptions options, out string bucket, out string? missing)
    {
        options = new ObjectStorageOptions();
        bucket = string.Empty;

        var accountId = Environment.GetEnvironmentVariable("SANKORE_R2_ACCOUNT_ID");
        var accessKeyId = Environment.GetEnvironmentVariable("SANKORE_R2_ACCESS_KEY_ID");
        var secret = Environment.GetEnvironmentVariable("SANKORE_R2_SECRET_ACCESS_KEY");
        var bucketName = Environment.GetEnvironmentVariable("SANKORE_R2_BUCKET");

        missing =
            string.IsNullOrWhiteSpace(accountId) ? "SANKORE_R2_ACCOUNT_ID"
            : string.IsNullOrWhiteSpace(accessKeyId) ? "SANKORE_R2_ACCESS_KEY_ID"
            : string.IsNullOrWhiteSpace(secret) ? "SANKORE_R2_SECRET_ACCESS_KEY"
            : string.IsNullOrWhiteSpace(bucketName) ? "SANKORE_R2_BUCKET"
            : null;

        if (missing is not null) return false;

        options = new ObjectStorageOptions
        {
            AccountId = accountId,
            AccessKeyId = accessKeyId,
            SecretAccessKey = secret,

            // Lets the same test run against MinIO or another S3-compatible endpoint; unset for R2.
            ServiceUrl = Environment.GetEnvironmentVariable("SANKORE_R2_SERVICE_URL"),
        };

        bucket = bucketName!;
        return true;
    }

    private static async Task<List<string>> Collect(IAsyncEnumerable<string> keys)
    {
        var result = new List<string>();
        await foreach (var key in keys) result.Add(key);
        return result;
    }
}
