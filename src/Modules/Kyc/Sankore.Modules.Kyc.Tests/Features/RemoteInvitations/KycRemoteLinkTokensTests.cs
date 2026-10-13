namespace Sankore.Modules.Kyc.Tests.Features.RemoteInvitations;

using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Sankore.Modules.Kyc.Infrastructure.RemoteLinks;
using Xunit;

/// <summary>
/// The token is the only thing between the internet and one customer's identification page
/// (KRM-B-03), so what is asserted here is its shape and the two properties the link format exists
/// for.
/// </summary>
public sealed class KycRemoteLinkTokensTests
{
    private readonly IKycRemoteLinkTokens _tokens = new KycRemoteLinkTokens();

    [Fact]
    public void A_minted_token_is_43_url_safe_characters_and_never_repeats()
    {
        var minted = Enumerable.Range(0, 200).Select(_ => _tokens.Mint().Token).ToList();

        minted.Should().OnlyContain(t => t.Length == 43);
        minted.Should().OnlyContain(
            t => t.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_'),
            "'+' and '/' in a URL fragment are a support call waiting to happen");
        minted.Distinct(StringComparer.Ordinal).Should().HaveCount(200);
    }

    [Fact]
    public void The_hash_is_SHA_256_of_the_token_text()
    {
        // Pinned against the primitive rather than against itself: the hash in the database and the
        // hash a public endpoint computes from what the customer presents have to be the same
        // function, and "it matches what Mint produced" would pass even if both were wrong.
        var (token, hash) = _tokens.Mint();

        hash.Should().Equal(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        hash.Should().HaveCount(32);
    }

    [Fact]
    public void A_presented_token_hashes_to_the_same_digest()
    {
        var (token, hash) = _tokens.Mint();

        _tokens.HashOrNull(token).Should().Equal(hash);
        _tokens.HashOrNull($"  {token}  ").Should().Equal(hash,
            "a token pasted out of an email client arrives with whitespace");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("too-short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]      // 44 chars
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]       // right length, wrong alphabet
    public void A_value_that_cannot_be_one_of_our_tokens_is_rejected_without_a_query(string? candidate)
    {
        // The point is not security — a wrong digest simply misses the index — but that a flood of
        // junk tokens against the public endpoint never becomes a flood of database round trips.
        _tokens.HashOrNull(candidate).Should().BeNull();
    }

    [Fact]
    public void The_link_puts_the_token_in_the_fragment()
    {
        // A fragment is never sent to the server: the token cannot appear in an nginx access log,
        // in a proxy's log, or in the Referer of whatever the page loads next.
        var link = _tokens.BuildLink("imf.sankore.app", "abc");

        link.Should().Be("https://imf.sankore.app/kyc/verification#t=abc");
    }

    [Theory]
    [InlineData("imf.sankore.app")]
    [InlineData("https://imf.sankore.app")]
    [InlineData("http://imf.sankore.app")]
    [InlineData("imf.sankore.app/")]
    public void The_link_is_always_https_whatever_the_stored_fqdn_looks_like(string fqdn)
    {
        // Not cosmetic: a browser refuses getUserMedia on an insecure origin, so a link on http
        // opens a page whose camera never starts and whose failure says nothing about why. And a
        // stored value that already carries a scheme must not produce "https://https://".
        _tokens.BuildLink(fqdn, "abc")
            .Should().Be("https://imf.sankore.app/kyc/verification#t=abc");
    }
}
