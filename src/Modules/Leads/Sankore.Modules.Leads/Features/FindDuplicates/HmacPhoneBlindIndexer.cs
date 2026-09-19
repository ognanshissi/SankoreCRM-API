namespace Sankore.Modules.Leads.Features.FindDuplicates;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

/// <summary>
/// HMAC-SHA256 blind index on the normalized phone number (last 8 digits).
/// The key is read from <c>Leads:BlindIndexKey</c> (Base64-encoded, ≥ 32 bytes).
/// </summary>
internal sealed class HmacPhoneBlindIndexer : IPhoneBlindIndexer
{
    private readonly byte[] _key;

    public HmacPhoneBlindIndexer(IConfiguration config)
    {
        var keyBase64 = config["Leads:BlindIndexKey"]
            ?? throw new InvalidOperationException(
                "Leads:BlindIndexKey must be configured (Base64, ≥ 32 bytes). " +
                "Generate with: Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))");
        _key = Convert.FromBase64String(keyBase64);
    }

    public string Compute(string phoneNumber)
    {
        var normalized = IdentityMatchScorer.NormalizePhone(phoneNumber);
        using var hmac = new HMACSHA256(_key);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
