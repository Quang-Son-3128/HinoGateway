using System.Text;

namespace HinoGateway.Core;

/// <summary>Durably writes a same-directory temporary file before atomically publishing it.</summary>
public static class DurableAtomicFile
{
    public static void WriteAllText(string path, string contents, string? backupPath = null,
        Action<string>? prepareTemp = null, UnixFileMode? unixCreateMode = null) =>
        WriteAllBytes(path, new UTF8Encoding(false).GetBytes(contents), backupPath, prepareTemp, unixCreateMode);

    public static void WriteAllBytes(string path, ReadOnlySpan<byte> contents, string? backupPath = null,
        Action<string>? prepareTemp = null, UnixFileMode? unixCreateMode = null)
    {
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path)
                        ?? throw new InvalidOperationException($"'{path}' has no parent directory.");
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            CreateProtectedTemp(temp, prepareTemp, unixCreateMode);
            using (var stream = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.None,
                       64 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }
            Publish(temp, path, backupPath);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    public static Task WriteAllTextAsync(string path, string contents, CancellationToken ct = default,
        string? backupPath = null, Action<string>? prepareTemp = null, UnixFileMode? unixCreateMode = null) =>
        WriteAllBytesAsync(path, new UTF8Encoding(false).GetBytes(contents), ct, prepareTemp, unixCreateMode, backupPath);

    public static async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> contents,
        CancellationToken ct = default, Action<string>? prepareTemp = null,
        UnixFileMode? unixCreateMode = null, string? backupPath = null)
    {
        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path)
                        ?? throw new InvalidOperationException($"'{path}' has no parent directory.");
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            CreateProtectedTemp(temp, prepareTemp, unixCreateMode);
            await using (var stream = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.None,
                             64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(contents, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            // Same publish step as the synchronous overload. It used to be File.Move(overwrite:false),
            // which threw the moment the destination already existed — so this method could only ever
            // create a file, never update one, which is the entire point of an atomic write.
            Publish(temp, path, backupPath);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    /// <summary>
    /// Swap the finished temp file in for the real one in a single filesystem operation, so a reader
    /// (or a power cut) only ever sees the whole old file or the whole new one.
    /// </summary>
    private static void Publish(string temp, string path, string? backupPath)
    {
        if (File.Exists(path))
            File.Replace(temp, path, backupPath, ignoreMetadataErrors: true);
        else
            File.Move(temp, path);
    }

    private static void CreateProtectedTemp(string path, Action<string>? prepareTemp, UnixFileMode? unixCreateMode)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 1,
            Options = FileOptions.WriteThrough,
        };
        if (!OperatingSystem.IsWindows() && unixCreateMode is { } mode)
            options.UnixCreateMode = mode;
        using (new FileStream(path, options)) { }
        // On Windows this applies the protected ACL before any sensitive bytes are written.
        prepareTemp?.Invoke(path);
    }
}
