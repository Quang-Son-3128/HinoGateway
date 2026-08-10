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

        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path));
            if (loaded is not null)
                _values = new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
            _logger.LogInformation("Loaded {Count} secret(s) from {Path}", _values.Count, _path);
        }
        catch (Exception ex)
        {
            // Deliberately not fatal, unlike the project file: a gateway that cannot read its
            // secrets should still come up polling everything that needs none, with the affected
            // connections failing to authenticate and saying so.
            _logger.LogError(ex, "Could not read the secret file {Path}; treating it as empty", _path);
        }
    }

    private void Save()
    {
        var temp = _path + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(temp, JsonSerializer.Serialize(_values, Json));
            if (File.Exists(_path))
                File.Replace(temp, _path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(temp, _path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not write the secret file {Path} — the change is live but NOT persisted", _path);
            throw new ConfigurationPersistenceException(_path,
                $"Could not persist gateway credentials to '{_path}'. The change will not survive a restart.", ex);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch { /* best effort */ }
            }
        }
    }
}
