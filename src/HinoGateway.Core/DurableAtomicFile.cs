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
            if (File.Exists(path))
                File.Replace(temp, path, backupPath, ignoreMetadataErrors: true);
            else
                File.Move(temp, path);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    public static async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> contents,
        CancellationToken ct = default, Action<string>? prepareTemp = null,
        UnixFileMode? unixCreateMode = null)
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
            File.Move(temp, path, overwrite: false);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
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
