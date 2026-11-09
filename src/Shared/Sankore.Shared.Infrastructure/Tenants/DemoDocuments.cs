using System.Globalization;
using System.Text;

namespace Sankore.Shared.Infrastructure.Tenants;

/// <summary>
/// A one-page PDF written by hand — a title and lines of text in Helvetica — for the demo's documents
/// (<c>docs/donnees-demarrage-demo.md</c>: insurers' information sheets, identity documents of the
/// demo's KYC files). Just enough of the format for any reader to open it: five objects, their byte
/// offsets, a trailer. No library is referenced for so little, and each document says plainly that it
/// is a demonstration one.
/// </summary>
public static class DemoPdf
{
    private static readonly Encoding Latin1 = Encoding.Latin1;

    public static byte[] Page(string title, IReadOnlyList<string> lines)
    {
        var text = new StringBuilder();
        text.Append("BT /F1 18 Tf 56 780 Td ").Append(Literal(title)).Append(" Tj ET\n");
        var y = 744;
        foreach (var line in lines)
        {
            text.Append("BT /F1 11 Tf 56 ").Append(y.ToString(CultureInfo.InvariantCulture))
                .Append(" Td ").Append(Literal(line)).Append(" Tj ET\n");
            y -= 18;
        }
        var stream = Latin1.GetBytes(text.ToString());

        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };

        using var pdf = new MemoryStream();
        void Write(string s) { var b = Latin1.GetBytes(s); pdf.Write(b, 0, b.Length); }

        Write("%PDF-1.4\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Position);
            Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        offsets.Add(pdf.Position);
        Write($"5 0 obj\n<< /Length {stream.Length} >>\nstream\n");
        pdf.Write(stream, 0, stream.Length);
        Write("\nendstream\nendobj\n");

        var xref = pdf.Position;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            Write($"{offset.ToString("D10", CultureInfo.InvariantCulture)} 00000 n \n");
        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return pdf.ToArray();
    }

    /// <summary>A PDF string: Latin-1 as WinAnsi, the three delimiters escaped, the rest replaced.</summary>
    private static string Literal(string value)
    {
        var s = new StringBuilder("(");
        // French number formatting groups thousands with a narrow no-break space (U+202F), which
        // WinAnsi lacks: « 2?000 FCFA » until it was mapped to a plain space.
        foreach (var c in value.Replace('—', '-').Replace('–', '-').Replace('’', '\'').Replace(' ', ' ').Replace(' ', ' '))
        {
            if (c is '\\' or '(' or ')') s.Append('\\');
            s.Append(c <= 255 ? c : '?');
        }
        return s.Append(')').ToString();
    }
}

/// <summary>
/// A grey head-and-shoulders silhouette as a PNG, for the demo's selfies: an image the KYC store
/// accepts and a screen can show, of nobody. Written by hand (IHDR, one zlib IDAT, IEND) like
/// <see cref="DemoPdf"/>.
/// </summary>
public static class DemoSilhouette
{
    public static byte[] Png(int width = 240, int height = 300)
    {
        var raw = new byte[height * (1 + width * 3)];
        var i = 0;
        for (var y = 0; y < height; y++)
        {
            raw[i++] = 0; // filter: none
            for (var x = 0; x < width; x++)
            {
                double hx = x - width / 2.0, hy = y - height * 0.37, sx = x - width / 2.0, sy = y - height;
                var head = hx * hx + hy * hy <= Math.Pow(width * 0.21, 2);
                var shoulders = sx * sx / Math.Pow(width * 0.42, 2) + sy * sy / Math.Pow(height * 0.36, 2) <= 1;
                var shade = head || shoulders ? (byte)0x9C : (byte)0xE5;
                raw[i++] = shade; raw[i++] = shade; raw[i++] = (byte)(shade + 6);
            }
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 2; // 8 bits, truecolour
        Chunk(png, "IHDR", header);
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(raw);
            Chunk(png, "IDAT", compressed.ToArray());
        }
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        png.Write(length);
        var typed = new byte[4 + data.Length];
        Encoding.ASCII.GetBytes(type).CopyTo(typed, 0);
        data.CopyTo(typed, 4);
        png.Write(typed);
        Span<byte> crc = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
        png.Write(crc);
    }

    /// <summary>The PNG chunk checksum (ISO 3309), over the type and the data.</summary>
    private static uint Crc32(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
