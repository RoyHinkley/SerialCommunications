using System.Text;

namespace SerialCommunications.Serial;

/// <summary>Produces unambiguous printable representations of arbitrary binary data.</summary>
/// <remarks>
/// Text rendering is intentionally ASCII-oriented. Bytes 0x20-0x7E are printable ASCII;
/// selected ASCII controls use familiar escapes and all remaining byte values use \xHH.
/// This class does not encode arbitrary .NET strings into bytes.
/// </remarks>
public static class BinaryDataFormatter
{
    /// <summary>Formats bytes as printable ASCII plus lossless backslash escapes.</summary>
    public static string ToEscapedText(ReadOnlySpan<byte> bytes)
    {
        var result = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
        {
            switch (b)
            {
                case (byte)'\\': result.Append("\\\\"); break;
                case (byte)'\r': result.Append("\\r"); break;
                case (byte)'\n': result.Append("\\n"); break;
                case (byte)'\t': result.Append("\\t"); break;
                default:
                    if (b is >= 0x20 and <= 0x7E) result.Append((char)b);
                    else result.Append($"\\x{b:X2}");
                    break;
            }
        }
        return result.ToString();
    }

    /// <summary>Formats bytes as space-separated two-digit hexadecimal values.</summary>
    /// <remarks>.NET provides hexadecimal conversion, but not this diagnostic representation with byte separators.</remarks>
    public static string ToHexBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return string.Empty;
        var result = new StringBuilder(bytes.Length * 3 - 1);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (i > 0) result.Append(' ');
            result.Append(bytes[i].ToString("X2"));
        }
        return result.ToString();
    }
}
