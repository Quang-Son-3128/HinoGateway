using System.Text.Json;
using HinoGateway.Core;
using Microsoft.Extensions.Logging;

namespace HinoGateway.Storage;

/// <summary>
/// The gateway's secret values, in a file of their own (<c>hino.secrets.json</c>) next to the project
/// file — never inside it, never in a <c>.hino</c> export, and not something the operator is expected
/// to copy when moving a configuration.
///
/// It is <b>not encryption</b>, and it does not pretend to be: anyone who can read the file can read
/// the passwords, exactly as with any local service account file. What it buys is that the file which
/// travels — the project, the export, the backup someone mails — no longer carries credentials.
/// Machine-bound encryption (DPAPI) would be Windows-only, and this gateway is meant to run on Linux
/// IPCs too; where a platform already manages credentials, use <c>${env:NAME}</c> references instead
/// and store nothing here at all.
/// </summary>
public sealed class JsonSecretStore : ISecretStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public JsonSecretStore(string path, ILogger<JsonSecretStore> logger)
    {
        _path = Path.GetFullPath(path);
        _logger = logger;
        Load();
    }

    public string FilePath => _path;

    public IReadOnlyCollection<string> Names
    {
        get { lock (_gate) return _values.Keys.ToList(); }
    }

    public bool TryGet(string name, out string value)
    {
        lock (_gate)
            return _values.TryGetValue(name, out value!);
    }

    public void Set(string name, string value)
    {
        lock (_gate)
        {
            _values[name] = value;
            Save();
        }
    }

    public bool Delete(string name)
    {
        lock (_gate)
        {
            if (!_values.Remove(name))
                return false;
            Save();
            return true;
        }
    }

    private void Load()
    {
        if (!File.Exists(_path))
            return;

        if (!TryLoad(_path, primary: true) && File.Exists(_path + ".bak") && TryLoad(_path + ".bak", primary: false))
            _logger.LogWarning("Gateway credentials were restored from the backup {Path}.bak; " +
                               "the primary file was unusable", _path);

        try { HardenPermissions(_path); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not restrict permissions on secret file {Path}", _path); }
    }

    /// <summary>
    /// Read one credential file. Failure is deliberately not fatal, unlike the project file: a
    /// gateway that cannot read its secrets should still come up polling everything that needs none,
    /// with the affected connections failing to authenticate and saying so.
    /// </summary>
    private bool TryLoad(string path, bool primary)
    {
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            if (loaded is null)
                return false;
            _values = new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
            _logger.LogInformation("Loaded {Count} secret(s) from {Path}", _values.Count, path);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Log(primary ? LogLevel.Warning : LogLevel.Error, ex,
                "Could not read the secret file {Path}", path);
            return false;
        }
    }

    private void Save()
    {
        try
        {
            // The backup path was missing here while the project file, the retained values and the
            // energy baselines all had one. That left the credentials as the only durable state in
            // the gateway with nothing behind it: a file lost to a power cut took every PLC and
            // broker password with it, and the only way back was an engineer retyping them on site.
            DurableAtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_values, Json),
                backupPath: _path + ".bak",
                prepareTemp: HardenPermissions,
                unixCreateMode: UnixFileMode.UserRead | UnixFileMode.UserWrite);
            HardenPermissions(_path);
            // The backup holds the same passwords, so it gets the same ACL. File.Replace carries the
            // displaced file's permissions over, but the very first save creates it by other means.
            if (File.Exists(_path + ".bak"))
                HardenPermissions(_path + ".bak");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not write the secret file {Path} — the change is live but NOT persisted", _path);
            throw new ConfigurationPersistenceException(_path,
                $"Could not persist gateway credentials to '{_path}'. The change will not survive a restart.", ex);
        }
    }

    private static void HardenPermissions(string path)
    {
        if (OperatingSystem.IsWindows()) HardenWindowsAcl(path);
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void HardenWindowsAcl(string path)
    {
        var identity = System.Security.Principal.WindowsIdentity.GetCurrent().User
                       ?? throw new InvalidOperationException("The service account has no Windows SID.");
        var acl = new System.Security.AccessControl.FileSecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(identity,
            System.Security.AccessControl.FileSystemRights.FullControl,
            System.Security.AccessControl.AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(acl);
    }
}
