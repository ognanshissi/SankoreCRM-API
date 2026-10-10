namespace Sankore.Modules.Integration.Tests.Infrastructure.Transport;

using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Sankore.Modules.Integration.Infrastructure.Transport;
using Xunit;

/// <summary>
/// The fingerprint comparison that is the only thing standing between a deposit and a
/// man-in-the-middle.
///
/// <para>
/// Pinned on its own because "we trust whatever answers" is not a failure any integration test
/// would notice: the transfer succeeds, the log says deposited, and a bank's customer file has
/// gone to whoever answered on port 22.
/// </para>
/// </summary>
public sealed class SftpHostKeyFingerprintTests
{
    /// <summary>A stand-in for a server's host key blob. Its content does not matter, its hash does.</summary>
    private static readonly byte[] HostKey =
        Encoding.ASCII.GetBytes("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIexample");

    private static byte[] Digest => SHA256.HashData(HostKey);

    private static string Base64Unpadded => Convert.ToBase64String(Digest).TrimEnd('=');

    private static string Hex => Convert.ToHexString(Digest).ToLowerInvariant();

    [Fact]
    public void The_ssh_keygen_spelling_matches()
    {
        // What `ssh-keyscan host | ssh-keygen -lf -` prints: unpadded base64 behind a SHA256:
        // prefix. An operator who has to re-encode a fingerprint by hand eventually pastes the
        // wrong one, so every spelling they can obtain is accepted.
        SftpHostKeyFingerprint.Matches(HostKey, $"SHA256:{Base64Unpadded}").Should().BeTrue();
        SftpHostKeyFingerprint.Matches(HostKey, Base64Unpadded).Should().BeTrue();
        SftpHostKeyFingerprint.Matches(HostKey, Convert.ToBase64String(Digest)).Should().BeTrue();
        SftpHostKeyFingerprint.Matches(HostKey, $"  sha256:{Base64Unpadded}  ").Should().BeTrue();
    }

    [Fact]
    public void The_hex_spelling_matches_with_or_without_colons_and_in_either_case()
    {
        SftpHostKeyFingerprint.Matches(HostKey, Hex).Should().BeTrue();
        SftpHostKeyFingerprint.Matches(HostKey, Hex.ToUpperInvariant()).Should().BeTrue();

        var colonised = string.Join(
            ':', Enumerable.Range(0, Digest.Length).Select(i => Hex.Substring(i * 2, 2)));

        SftpHostKeyFingerprint.Matches(HostKey, colonised).Should().BeTrue();
    }

    [Fact]
    public void A_different_key_does_not_match()
    {
        var otherKey = Encoding.ASCII.GetBytes("ssh-rsa AAAAB3NzaC1yc2Esomethingelse");

        SftpHostKeyFingerprint.Matches(otherKey, $"SHA256:{Base64Unpadded}").Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_configured_fingerprint_is_a_refusal_and_never_an_acceptance(string? configured)
    {
        // The fail-closed rule lives here rather than at the call site, so a second call site
        // cannot forget it.
        SftpHostKeyFingerprint.Matches(HostKey, configured).Should().BeFalse();
    }

    [Fact]
    public void An_absent_host_key_is_a_refusal()
    {
        SftpHostKeyFingerprint.Matches(null, $"SHA256:{Base64Unpadded}").Should().BeFalse();
        SftpHostKeyFingerprint.Matches([], $"SHA256:{Base64Unpadded}").Should().BeFalse();
    }

    [Fact]
    public void An_md5_fingerprint_is_refused_rather_than_accepted_as_a_weaker_check()
    {
        // The legacy `ssh-keygen -E md5` form, which some runbooks still carry. Accepting it would
        // let a collision-prone digest authorise the connection that carries a tenant's whole
        // customer file.
        // CA5351 is right about MD5 and wrong about this line: the digest is computed in order to
        // prove the guard REFUSES it. Suppressing it narrowly, with the reason, rather than
        // switching the project off the rule — which would also stop flagging a real use.
#pragma warning disable CA5351 // Do Not Use Broken Cryptographic Algorithms
        var md5 = Convert.ToHexString(MD5.HashData(HostKey)).ToLowerInvariant();
#pragma warning restore CA5351
        var colonisedMd5 = string.Join(
            ':', Enumerable.Range(0, 16).Select(i => md5.Substring(i * 2, 2)));

        SftpHostKeyFingerprint.Matches(HostKey, md5).Should().BeFalse();
        SftpHostKeyFingerprint.Matches(HostKey, colonisedMd5).Should().BeFalse();
        SftpHostKeyFingerprint.Matches(HostKey, $"MD5:{colonisedMd5}").Should().BeFalse();
    }

    [Theory]
    [InlineData("not a fingerprint at all")]
    [InlineData("SHA256:!!!!!")]
    [InlineData("deadbeef")]
    [InlineData("SHA256:c2hvcnQ=")]
    public void A_malformed_fingerprint_is_refused_rather_than_ignored(string configured)
    {
        SftpHostKeyFingerprint.Matches(HostKey, configured).Should().BeFalse(
            "an unparseable fingerprint must fail closed, not fall through to trusting the key");
    }
}
