using HinoGateway.Core;

namespace HinoGateway.Engine.Tests;

public class DurableAtomicFileTests
{
    [Fact]
    public void Replacement_is_complete_and_keeps_the_previous_version()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hino-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "project.json");
            var backup = path + ".bak";
            DurableAtomicFile.WriteAllText(path, "old");
            DurableAtomicFile.WriteAllText(path, "new", backup);
            Assert.Equal("new", File.ReadAllText(path));
            Assert.Equal("old", File.ReadAllText(backup));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Temporary_file_is_prepared_before_contents_are_written()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hino-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var preparedEmpty = false;
            var path = Path.Combine(directory, "secret.json");
            DurableAtomicFile.WriteAllText(path, "sensitive", prepareTemp: temp =>
            {
                preparedEmpty = new FileInfo(temp).Length == 0;
            }, unixCreateMode: UnixFileMode.UserRead | UnixFileMode.UserWrite);

            Assert.True(preparedEmpty);
            Assert.Equal("sensitive", File.ReadAllText(path));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    /// <summary>
    /// The asynchronous overload published with File.Move(overwrite:false), so it threw on the second
    /// write instead of replacing the file. Anything built on it — the energy state snapshot rewritten
    /// every time a period rolls over — would have failed exactly once and then never saved again.
    /// </summary>
    [Fact]
    public async Task An_async_write_replaces_an_existing_file_instead_of_failing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hino-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "energy.json");
            var backup = path + ".bak";

            await DurableAtomicFile.WriteAllTextAsync(path, "old");
            await DurableAtomicFile.WriteAllTextAsync(path, "new", backupPath: backup);
            await DurableAtomicFile.WriteAllTextAsync(path, "newer", backupPath: backup);

            Assert.Equal("newer", File.ReadAllText(path));
            Assert.Equal("new", File.ReadAllText(backup));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
