using System.Security.Cryptography;

namespace MusicLib.Core;

public static class FileHasher
{
    /// <summary>Streaming SHA-256 of the whole file, lowercase hex.</summary>
    public static string ComputeHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 16, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
