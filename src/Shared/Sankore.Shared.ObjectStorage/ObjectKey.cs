namespace Sankore.Shared.ObjectStorage;

/// <summary>
/// The one definition of what a legal object key is, shared by every backend.
///
/// <para>
/// It <em>validates</em> and never sanitises, deliberately. A sanitiser answers "here is a key I
/// made safe", which means a caller that built <c>"../../etc/passwd"</c> gets an object back
/// under some other name and never learns it asked for the wrong thing; a validator answers "that
/// is not a key", which is the only answer that keeps the caller's bug visible. It is also why
/// this is enforced here rather than only in the filesystem backend: on S3 a <c>..</c> segment is
/// a perfectly ordinary key, so the same input would silently mean two different objects
/// depending on where the deployment stores its files, and a migration between the two would
/// quietly lose them.
/// </para>
/// </summary>
public static class ObjectKey
{
    /// <summary>S3's own ceiling. The filesystem's is lower on some platforms; this is the floor both share.</summary>
    public const int MaxLength = 1024;

    /// <summary>
    /// <c>true</c> when <paramref name="objectKey"/> is usable on every backend.
    ///
    /// <para>
    /// Rejected: empty or blank, longer than <see cref="MaxLength"/>, a leading or trailing
    /// <c>/</c>, an empty segment (<c>a//b</c>), a <c>.</c> or <c>..</c> segment, a backslash
    /// (a directory separator on Windows and an ordinary character on S3 — the exact kind of
    /// difference this class exists to remove), a colon (an NTFS alternate data stream), a
    /// control character, and any of the wildcard/redirect characters a shell or a URL would
    /// reinterpret.
    /// </para>
    /// </summary>
    public static bool IsValid(string? objectKey)
    {
        if (string.IsNullOrWhiteSpace(objectKey)) return false;
        if (objectKey.Length > MaxLength) return false;
        if (objectKey[0] == '/' || objectKey[^1] == '/') return false;

        foreach (var c in objectKey)
        {
            if (char.IsControl(c)) return false;
            if (c is '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|') return false;
        }

        foreach (var segment in objectKey.Split('/'))
        {
            if (segment.Length == 0) return false;
            if (segment is "." or "..") return false;
        }

        return true;
    }

    /// <summary>
    /// Throws <see cref="ArgumentException"/> unless <see cref="IsValid"/>. Used on the write and
    /// list paths, where a bad key is the caller's own bug and must surface as one.
    /// </summary>
    public static void Validate(string? objectKey, string paramName = "objectKey")
    {
        if (!IsValid(objectKey))
            throw new ArgumentException(
                $"'{objectKey}' is not a valid object key: keys are non-empty, at most "
                + $"{MaxLength} characters, '/'-separated with no empty, '.' or '..' segment, and "
                + "contain no backslash, colon, control or wildcard character.",
                paramName);
    }
}
