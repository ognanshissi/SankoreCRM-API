namespace Sankore.Shared.Infrastructure.Tests.ObjectStorage;

using System.Net;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sankore.Shared.ObjectStorage;
using Xunit;

/// <summary>
/// An S3 service held in a dictionary, substituted for the real one by overriding
/// <see cref="AmazonS3Client"/>'s operations — every one of them is <c>virtual</c>, and nothing
/// below them runs when they are replaced, so no socket is opened.
///
/// <para>
/// It exists so <see cref="R2ObjectBackend"/> can be put through the shared contract suite
/// without a bucket. It reproduces the four S3 behaviours the backend is built on, and only
/// those: a missing key is a 404 carrying <c>NoSuchKey</c>, a <c>PutObject</c> carrying
/// <c>If-None-Match: *</c> over an existing key is a 412, a <c>DeleteObject</c> succeeds whether
/// or not the key existed, and <c>ListObjectsV2</c> pages behind a continuation token.
/// </para>
///
/// <para>
/// <b>What it cannot say anything about</b> is whether Cloudflare R2 agrees — about checksum
/// trailers, about honouring the conditional write, about chunked payload signing. A double
/// written from the same understanding as the code it stands in for cannot disprove that
/// understanding. Only <c>R2ObjectBackendIntegrationTests</c>, against a real bucket, can.
/// </para>
/// </summary>
internal sealed class FakeS3 : AmazonS3Client
{
    private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
    private readonly List<TrackingStream> _streams = [];

    public FakeS3()
        : base(
            new BasicAWSCredentials("fake-access-key", "fake-secret-key"),
            new AmazonS3Config
            {
                ServiceURL = "https://fake.invalid",
                ForcePathStyle = true,
                AuthenticationRegion = "auto",
            })
    {
    }

    /// <summary>Small on purpose, so the contract suite's three keys cross a page boundary.</summary>
    public int MaxKeysPerPage { get; set; } = 2;

    /// <summary>Set to make every operation fail as a mistyped bucket would.</summary>
    public bool BucketMissing { get; set; }

    /// <summary>What a provider that ignored <c>If-None-Match</c> would do — the regression this guards.</summary>
    public bool HonoursIfNoneMatch { get; set; } = true;

    public int ListCallCount { get; private set; }

    /// <summary>Bytes actually pulled off a response body, across every GET.</summary>
    public long BytesStreamed => _streams.Sum(s => s.BytesRead);

    public IReadOnlyCollection<string> Keys => [.. _objects.Keys];

    public void PutDirectly(string key, byte[] content) => _objects[key] = content;

    public override Task<PutObjectResponse> PutObjectAsync(
        PutObjectRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfBucketMissing();

        if (HonoursIfNoneMatch && request.IfNoneMatch == "*" && _objects.ContainsKey(request.Key))
            throw Error(
                "At least one of the pre-conditions you specified did not hold",
                HttpStatusCode.PreconditionFailed, "PreconditionFailed");

        using var buffer = new MemoryStream();
        request.InputStream.CopyTo(buffer);
        _objects[request.Key] = buffer.ToArray();

        return Task.FromResult(new PutObjectResponse { HttpStatusCode = HttpStatusCode.OK });
    }

    public override Task<GetObjectResponse> GetObjectAsync(
        GetObjectRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfBucketMissing();

        if (!_objects.TryGetValue(request.Key, out var bytes)) throw NoSuchKey();

        var stream = new TrackingStream(bytes);
        _streams.Add(stream);

        return Task.FromResult(new GetObjectResponse
        {
            BucketName = request.BucketName,
            Key = request.Key,
            ContentLength = bytes.Length,
            ResponseStream = stream,
            HttpStatusCode = HttpStatusCode.OK,
        });
    }

    public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(
        GetObjectMetadataRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfBucketMissing();

        if (!_objects.TryGetValue(request.Key, out var bytes)) throw NoSuchKey();

        return Task.FromResult(new GetObjectMetadataResponse
        {
            ContentLength = bytes.Length,
            HttpStatusCode = HttpStatusCode.OK,
        });
    }

    /// <summary>Succeeds whether or not the key was there — the reason the backend asks first.</summary>
    public override Task<DeleteObjectResponse> DeleteObjectAsync(
        DeleteObjectRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfBucketMissing();
        _objects.Remove(request.Key);

        return Task.FromResult(new DeleteObjectResponse { HttpStatusCode = HttpStatusCode.NoContent });
    }

    public override Task<ListObjectsV2Response> ListObjectsV2Async(
        ListObjectsV2Request request, CancellationToken cancellationToken = default)
    {
        ThrowIfBucketMissing();
        ListCallCount++;

        var prefix = request.Prefix ?? string.Empty;

        var matching = _objects.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        // The continuation token is the last key handed out, which is what S3's own
        // lexicographic paging amounts to.
        var start = request.ContinuationToken is null
            ? 0
            : matching.FindIndex(k => string.CompareOrdinal(k, request.ContinuationToken) > 0) switch
            {
                < 0 => matching.Count,
                var i => i,
            };

        var page = matching.Skip(start).Take(MaxKeysPerPage).ToList();
        var truncated = start + page.Count < matching.Count;

        return Task.FromResult(new ListObjectsV2Response
        {
            Name = request.BucketName,
            Prefix = prefix,
            KeyCount = page.Count,
            IsTruncated = truncated,
            NextContinuationToken = truncated ? page[^1] : null,
            S3Objects = [.. page.Select(k => new S3Object { Key = k, Size = _objects[k].Length })],
            HttpStatusCode = HttpStatusCode.OK,
        });
    }

    private void ThrowIfBucketMissing()
    {
        if (BucketMissing)
            throw Error("The specified bucket does not exist", HttpStatusCode.NotFound, "NoSuchBucket");
    }

    private static AmazonS3Exception NoSuchKey() =>
        Error("The specified key does not exist.", HttpStatusCode.NotFound, "NoSuchKey");

    private static AmazonS3Exception Error(string message, HttpStatusCode status, string code) =>
        new(message) { StatusCode = status, ErrorCode = code };

    /// <summary>Counts what is read, so a test can assert a body was never touched.</summary>
    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public long BytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = base.Read(buffer);
            BytesRead += read;
            return read;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var read = base.Read(buffer.Span);
            BytesRead += read;
            return ValueTask.FromResult(read);
        }
    }
}

/// <summary>
/// <see cref="R2ObjectBackend"/> against the shared contract, over <see cref="FakeS3"/>.
/// Inherited rather than restated, for the reason the suite itself gives: the moment the backends
/// disagree about what "absent", "too large" or "already used" means, every test that uses a
/// cheaper one stops saying anything about this one.
/// </summary>
public sealed class R2ObjectBackendContractTests : ObjectBackendContractTests, IDisposable
{
    private readonly List<FakeS3> _clients = [];

    protected override IObjectBackend CreateBackend()
    {
        var s3 = new FakeS3();
        _clients.Add(s3);
        return new R2ObjectBackend(s3, "kyc-documents", NullLogger<R2ObjectBackend>.Instance);
    }

    public void Dispose()
    {
        foreach (var client in _clients) client.Dispose();
    }
}

/// <summary>
/// The parts of the S3 mapping the shared contract cannot see: that the ceiling is read from a
/// header, that a mistyped bucket is not an absent object, that listing pages, and that the
/// conditional write is what refuses a reused key.
/// </summary>
public sealed class R2ObjectBackendTests : IDisposable
{
    private readonly FakeS3 _s3 = new();
    private readonly R2ObjectBackend _sut;

    public R2ObjectBackendTests() =>
        _sut = new R2ObjectBackend(_s3, "kyc-documents", NullLogger<R2ObjectBackend>.Instance);

    public void Dispose()
    {
        _sut.Dispose();
        _s3.Dispose();
    }

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public async Task An_oversized_object_should_be_refused_from_its_header_without_reading_the_body()
    {
        await _sut.PutAsync("a/big.bin", new byte[4096]);

        (await _sut.GetAsync("a/big.bin", 1024)).Should().BeNull();

        _s3.BytesStreamed.Should().Be(0,
            "the ceiling is decided from Content-Length; fetching then measuring would make "
            + "maxBytes a lie, because the allocation it exists to prevent has already happened");
    }

    [Fact]
    public async Task A_mistyped_bucket_should_surface_rather_than_read_as_an_empty_store()
    {
        _s3.BucketMissing = true;

        var read = () => _sut.GetAsync("a/one.bin", long.MaxValue);
        var delete = () => _sut.DeleteAsync("a/one.bin");

        await read.Should().ThrowAsync<AmazonS3Exception>(
            "NoSuchBucket is a configuration fault; swallowing it would report every document as absent");
        await delete.Should().ThrowAsync<AmazonS3Exception>();
    }

    [Fact]
    public async Task Listing_should_follow_the_continuation_token_to_the_end()
    {
        _s3.MaxKeysPerPage = 2;

        for (var i = 0; i < 7; i++) await _sut.PutAsync($"t1/{i}.bin", Bytes($"{i}"));

        var keys = new List<string>();
        await foreach (var key in _sut.ListAsync("t1/")) keys.Add(key);

        keys.Should().HaveCount(7);
        _s3.ListCallCount.Should().Be(4, "7 keys at 2 per page is four round trips, not one");
    }

    [Fact]
    public async Task Listing_should_skip_a_directory_marker()
    {
        // A zero-byte object whose key ends in '/', which several S3 tools create to make a
        // bucket look like a tree. No Get resolves it, so handing one to the migration would
        // look like a document that vanished between listing and copying.
        _s3.PutDirectly("t1/folder/", []);
        await _sut.PutAsync("t1/folder/real.bin", Bytes("x"));

        var keys = new List<string>();
        await foreach (var key in _sut.ListAsync("t1/")) keys.Add(key);

        keys.Should().Equal("t1/folder/real.bin");
    }

    [Fact]
    public async Task A_reused_key_should_be_refused_by_the_conditional_write_not_by_a_prior_read()
    {
        await _sut.PutAsync("a/once.bin", Bytes("first"));

        var act = () => _sut.PutAsync("a/once.bin", Bytes("second"));

        await act.Should().ThrowAsync<InvalidOperationException>();

        // The guard that matters: a provider that ignored If-None-Match would overwrite silently,
        // and nothing above this line would notice. Simulated here; proven only against a real
        // bucket, by R2ObjectBackendIntegrationTests.
        _s3.HonoursIfNoneMatch = false;
        await _sut.PutAsync("a/once.bin", Bytes("second"));
        (await _sut.GetAsync("a/once.bin", long.MaxValue)).Should().Equal(Bytes("second"),
            "this is what a provider that ignores the condition does — the integration test is "
            + "the only thing that can prove R2 does not");
    }

    [Fact]
    public async Task A_malformed_key_should_never_reach_the_service_on_a_read()
    {
        _s3.BucketMissing = true; // any call at all would now throw

        foreach (var key in new[] { "", "   ", "/etc/passwd", "../../etc/passwd", "a\n/b" })
        {
            (await _sut.GetAsync(key, long.MaxValue)).Should().BeNull();
            (await _sut.DeleteAsync(key)).Should().BeFalse();
        }
    }

    [Fact]
    public void The_bucket_is_the_concern_and_is_visible_for_diagnostics()
        => _sut.BucketName.Should().Be("kyc-documents");
}

public sealed class ObjectStorageOptionsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e =>
                new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    private const string Section = "ObjectStorage:R2";

    [Fact]
    public void An_absent_section_should_read_as_not_configured()
    {
        var options = ObjectStorageOptions.FromConfiguration(Config());

        options.IsConfigured.Should().BeFalse();
    }

    [Fact]
    public void A_blank_value_should_not_count_as_configured()
    {
        var options = ObjectStorageOptions.FromConfiguration(Config(($"{Section}:AccountId", "   ")));

        options.IsConfigured.Should().BeFalse();
        options.AccountId.Should().BeNull();
    }

    [Fact]
    public void A_single_value_should_count_as_configured_so_a_half_filled_section_can_fail_loudly()
    {
        var options = ObjectStorageOptions.FromConfiguration(Config(($"{Section}:AccountId", "abc123")));

        options.IsConfigured.Should().BeTrue(
            "a section that is half filled in is a mistake, not a choice to use the filesystem");
    }

    [Fact]
    public void The_service_url_should_be_derived_from_the_account_id()
    {
        var options = new ObjectStorageOptions { AccountId = "d41d8cd98f00b204e9800998ecf8427e" };

        options.ResolveServiceUrl().Should()
            .Be("https://d41d8cd98f00b204e9800998ecf8427e.r2.cloudflarestorage.com");
    }

    [Fact]
    public void An_explicit_service_url_should_win_over_the_derived_one_and_lose_its_trailing_slash()
    {
        var options = new ObjectStorageOptions
        {
            AccountId = "abc123",
            ServiceUrl = "https://minio.internal:9000/",
        };

        options.ResolveServiceUrl().Should().Be("https://minio.internal:9000");
    }

    [Theory]
    [InlineData("AccessKeyId", "ObjectStorage:R2:AccessKeyId")]
    [InlineData("SecretAccessKey", "ObjectStorage:R2:SecretAccessKey")]
    [InlineData("AccountId", "ObjectStorage:R2:AccountId")]
    public void A_missing_credential_should_name_the_configuration_key_to_set(string omit, string expected)
    {
        var options = new ObjectStorageOptions
        {
            AccountId = omit == "AccountId" ? null : "abc123",
            AccessKeyId = omit == "AccessKeyId" ? null : "ak",
            SecretAccessKey = omit == "SecretAccessKey" ? null : "sk",
        };

        var act = () => options.Validate();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{expected}*",
                "the precedent is KycDocumentStore.ReadKey: a missing key must not surface as "
                + "'ArgumentNullException (Parameter \\'s\\')' halfway through a request");
    }

    [Fact]
    public void A_service_url_should_stand_in_for_a_missing_account_id()
    {
        var options = new ObjectStorageOptions
        {
            ServiceUrl = "https://minio.internal:9000",
            AccessKeyId = "ak",
            SecretAccessKey = "sk",
        };

        var act = () => options.Validate();

        act.Should().NotThrow();
    }

    [Fact]
    public void A_service_url_that_is_not_an_absolute_http_url_should_be_refused_by_name()
    {
        var options = new ObjectStorageOptions
        {
            ServiceUrl = "r2.cloudflarestorage.com",
            AccessKeyId = "ak",
            SecretAccessKey = "sk",
        };

        var act = () => options.Validate();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ObjectStorage:R2:ServiceUrl*");
    }

    [Fact]
    public void An_endpoint_pasted_into_the_account_id_should_be_refused_by_name()
    {
        // Otherwise the derived endpoint is
        // "https://https://x.r2.cloudflarestorage.com.r2.cloudflarestorage.com", whose DNS
        // failure names neither setting.
        var options = new ObjectStorageOptions
        {
            AccountId = "https://x.r2.cloudflarestorage.com",
            AccessKeyId = "ak",
            SecretAccessKey = "sk",
        };

        var act = () => options.Validate();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ObjectStorage:R2:AccountId*ServiceUrl*");
    }
}

public sealed class AddObjectBackendTests
{
    private const string Section = "ObjectStorage:R2";

    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e =>
                new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    private static IConfiguration FullyConfigured() => Config(
        ($"{Section}:AccountId", "d41d8cd98f00b204e9800998ecf8427e"),
        ($"{Section}:AccessKeyId", "ak"),
        ($"{Section}:SecretAccessKey", "sk"));

    private static IObjectBackend Resolve(IConfiguration config, string concern, string fallback)
    {
        var services = new ServiceCollection();
        services.AddObjectBackend(config, concern, fallback);

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredKeyedService<IObjectBackend>(concern);
    }

    [Fact]
    public void With_no_bucket_configured_the_filesystem_backend_should_be_registered_on_the_fallback_path()
    {
        var fallback = Path.Combine(Path.GetTempPath(), "sankore-objects-fallback");

        var backend = Resolve(Config(), "kyc-documents", fallback);

        backend.Should().BeOfType<LocalObjectBackend>()
            .Which.BasePath.Should().Be(Path.TrimEndingDirectorySeparator(Path.GetFullPath(fallback)));
    }

    [Fact]
    public void With_a_bucket_configured_the_r2_backend_should_be_registered_on_the_concerns_bucket()
    {
        var backend = Resolve(FullyConfigured(), "kyc-documents", Path.GetTempPath());

        backend.Should().BeOfType<R2ObjectBackend>()
            .Which.BucketName.Should().Be("kyc-documents",
                "the concern name IS the bucket name — a second setting could drift from it");
    }

    [Fact]
    public void Two_concerns_should_resolve_to_two_buckets_in_the_same_container()
    {
        var services = new ServiceCollection();
        services.AddObjectBackend(FullyConfigured(), "kyc-documents", Path.GetTempPath());
        services.AddObjectBackend(FullyConfigured(), "imports", Path.GetTempPath());

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredKeyedService<IObjectBackend>("kyc-documents")
            .Should().BeOfType<R2ObjectBackend>().Which.BucketName.Should().Be("kyc-documents");
        provider.GetRequiredKeyedService<IObjectBackend>("imports")
            .Should().BeOfType<R2ObjectBackend>().Which.BucketName.Should().Be("imports");
    }

    [Fact]
    public void A_half_configured_section_should_fail_the_boot_rather_than_fall_back_to_the_filesystem()
    {
        var config = Config(($"{Section}:AccountId", "d41d8cd98f00b204e9800998ecf8427e"));

        var act = () => new ServiceCollection()
            .AddObjectBackend(config, "kyc-documents", Path.GetTempPath());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ObjectStorage:R2:AccessKeyId*",
                "silently becoming a filesystem deployment because a secret failed to be injected "
                + "writes KYC evidence to a volume the next redeploy discards");
    }

    [Theory]
    [InlineData("KYC-Documents")]  // upper case is not a legal bucket name
    [InlineData("ab")]             // too short
    [InlineData("-imports")]       // must start alphanumeric
    [InlineData("kyc_documents")]  // underscore
    public void A_concern_that_cannot_be_a_bucket_name_should_be_refused_at_registration(string concern)
    {
        var act = () => new ServiceCollection()
            .AddObjectBackend(FullyConfigured(), concern, Path.GetTempPath());

        act.Should().Throw<ArgumentException>().WithMessage($"*{concern}*");
    }

    [Fact]
    public void A_concern_that_cannot_be_a_bucket_name_should_still_be_allowed_without_a_bucket()
    {
        // Nothing is sent to a provider, so the name is only a DI key. Refusing it here would
        // make a local-only deployment fail over a constraint that does not apply to it.
        var act = () => new ServiceCollection()
            .AddObjectBackend(Config(), "KYC_Documents", Path.GetTempPath());

        act.Should().NotThrow();
    }

    [Fact]
    public void The_backend_should_be_a_singleton_so_one_s3_client_serves_the_process()
    {
        var services = new ServiceCollection();
        services.AddObjectBackend(FullyConfigured(), "kyc-documents", Path.GetTempPath());

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredKeyedService<IObjectBackend>("kyc-documents")
            .Should().BeSameAs(provider.GetRequiredKeyedService<IObjectBackend>("kyc-documents"));
    }

    [Fact]
    public void IsObjectStorageConfigured_should_answer_which_medium_a_host_is_about_to_use()
    {
        Config().IsObjectStorageConfigured().Should().BeFalse();
        FullyConfigured().IsObjectStorageConfigured().Should().BeTrue();
    }
}
