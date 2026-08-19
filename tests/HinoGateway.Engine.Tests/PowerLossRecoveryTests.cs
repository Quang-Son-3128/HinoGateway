using HinoGateway.Core;
using HinoGateway.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace HinoGateway.Engine.Tests;

/// <summary>
/// What has to be true the morning after the power came back.
///
/// <para>
/// Every durable file the gateway owns is written atomically with a <c>.bak</c> beside it, and for a
/// long time two of them wrote that backup and never read it — which is the same as not having one.
/// These tests corrupt the primary file the way an unclean shutdown does and assert that the gateway
/// comes back by itself, because the one moment a backup earns its keep is the moment nobody is on
/// site to restore it by hand.
/// </para>
/// </summary>
public class PowerLossRecoveryTests : IDisposable
{
    private readonly string _dir;

    public PowerLossRecoveryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "hino-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string Path_(string name) => Path.Combine(_dir, name);

    // --- project file --------------------------------------------------------

    private JsonProjectStore NewProjectStore(string path) => new(path, NullLogger<JsonProjectStore>.Instance);

    private static DeviceConfig Device(string id) => new()
    {
        DeviceId = id,
        Driver = "ModbusTCP",
        ScanInterval = TimeSpan.FromMilliseconds(500),
        Settings = new Dictionary<string, string> { ["Host"] = "127.0.0.1", ["Port"] = "502" },
    };

    /// <summary>Write a project, then a second one so that the first ends up in the .bak.</summary>
    private async Task<string> SeedProjectWithBackupAsync()
    {
        var path = Path_("hino.json");
        var store = NewProjectStore(path);
        await store.UpsertDeviceAsync(Device("FIRST"), default);
        await store.UpsertDeviceAsync(Device("SECOND"), default);
        Assert.True(File.Exists(path + ".bak"), "the second save should have left a backup");
        return path;
    }

    [Fact]
    public async Task A_truncated_project_file_is_recovered_from_the_backup()
    {
        var path = await SeedProjectWithBackupAsync();

        // A file cut off mid-write is what an unclean shutdown leaves behind.
        var whole = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, whole[..(whole.Length / 2)]);

        var reopened = NewProjectStore(path);

        Assert.NotNull(reopened.RecoveredFromBackup);
        var devices = await reopened.GetDevicesAsync(default);
        Assert.Contains(devices, d => d.DeviceId == "FIRST");
    }

    [Fact]
    public async Task The_unreadable_project_file_is_moved_aside_so_the_next_save_cannot_eat_the_backup()
    {
        var path = await SeedProjectWithBackupAsync();
        await File.WriteAllTextAsync(path, "{ this is not json");

        var reopened = NewProjectStore(path);
        Assert.NotNull(reopened.RecoveredFromBackup);

        // The corrupt bytes are kept for diagnosis...
        Assert.NotEmpty(Directory.GetFiles(_dir, "hino.json.corrupt-*"));

        // ...and, crucially, the next save must not push them into the .bak slot. DurableAtomicFile
        // publishes by replacing the primary and moving the displaced file to the backup path, so
        // leaving the corrupt primary in place would destroy the copy that just rescued the gateway.
        await reopened.UpsertDeviceAsync(Device("THIRD"), default);

        var afterSave = NewProjectStore(path);
        Assert.Null(afterSave.RecoveredFromBackup);
        var devices = await afterSave.GetDevicesAsync(default);
        Assert.Contains(devices, d => d.DeviceId == "THIRD");
        Assert.Contains(devices, d => d.DeviceId == "FIRST");
    }

    [Fact]
    public async Task A_project_file_that_was_never_backed_up_still_refuses_to_start_empty()
    {
        // Refusing is the correct outcome when there is nothing to fall back on: starting empty
        // would take a live gateway off the air and then overwrite the operator's file.
        var path = Path_("hino.json");
        var store = NewProjectStore(path);
        await store.UpsertDeviceAsync(Device("ONLY"), default);
        File.Delete(path + ".bak");
        await File.WriteAllTextAsync(path, "{ broken");

        var ex = Assert.Throws<InvalidOperationException>(() => NewProjectStore(path));
        Assert.Contains("backup", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_healthy_project_file_reports_no_recovery()
    {
        var path = await SeedProjectWithBackupAsync();
        var reopened = NewProjectStore(path);
        Assert.Null(reopened.RecoveredFromBackup);
        Assert.Empty(Directory.GetFiles(_dir, "hino.json.corrupt-*"));
    }

    // --- credentials ---------------------------------------------------------

    private JsonSecretStore NewSecretStore(string path) => new(path, NullLogger<JsonSecretStore>.Instance);

    [Fact]
    public void Credentials_are_recovered_from_the_backup_after_a_torn_write()
    {
        var path = Path_("hino.secrets.json");
        var store = NewSecretStore(path);
        store.Set("mqtt.broker", "first-password");
        store.Set("plc.line1", "second-password");

        Assert.True(File.Exists(path + ".bak"),
            "credentials were the only durable state with no backup at all; the second save must leave one");

        File.WriteAllText(path, "{ truncated");

        var reopened = NewSecretStore(path);
        Assert.True(reopened.TryGet("mqtt.broker", out var recovered));
        Assert.Equal("first-password", recovered);
    }

    [Fact]
    public void An_unreadable_credential_file_with_no_backup_still_lets_the_gateway_start()
    {
        // Unlike the project file, this one is deliberately non-fatal: a gateway that cannot read its
        // secrets should still come up polling every connection that needs none.
        var path = Path_("hino.secrets.json");
        File.WriteAllText(path, "not json at all");

        var store = NewSecretStore(path);
        Assert.False(store.TryGet("anything", out _));
    }
}
