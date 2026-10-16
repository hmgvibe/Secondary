using System.Text.Json;

namespace Secondary.Core;

// Read the same byte representation that is checked before an atomic save.
internal static class BoundedJson
{
    internal static readonly JsonSerializerOptions Options = new() { WriteIndented = true, MaxDepth = 32 };
    internal static T? Read<T>(string path, int maximumBytes, string sizeMessage)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximumBytes) throw new InvalidDataException(sizeMessage);
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        // Also reject a file that grew after the size check.
        if (stream.ReadByte() != -1) throw new InvalidDataException(sizeMessage);
        return JsonSerializer.Deserialize<T>(bytes, Options);
    }
    internal static byte[] Serialize<T>(T value, int maximumBytes, string sizeMessage)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (bytes.Length > maximumBytes) throw new InvalidDataException(sizeMessage);
        return bytes;
    }
}
