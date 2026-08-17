namespace HinoGateway.Server;

/// <summary>Stable writable locations, independent of executable and working directories.</summary>
public sealed record GatewayDataPaths(string Root)
{
    public string Project => Path.Combine(Root, "hino.json");
    public string Secrets => Path.Combine(Root, "hino.secrets.json");
    public string Retained => Path.Combine(Root, "hino.retain.json");
    public string Logs => Path.Combine(Root, "logs");
    public string Audit => Path.Combine(Root, "audit");
    public string Mqtt => Path.Combine(Root, "mqtt");
    public string History => Path.Combine(Root, "history");

    public static GatewayDataPaths Create(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration["Data:Root"];
        string root;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!Path.IsPathRooted(configured))
                throw new InvalidOperationException(
                    $"Data:Root must be an absolute path; relative value '{configured}' depends on the process working directory.");
            root = configured;
        }
        else if (environment.IsDevelopment())
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HinoGateway");
        }
        else if (OperatingSystem.IsWindows())
        {
            root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HinoGateway");
        }
        else
        {
            root = "/var/lib/hinogateway";
        }
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        PermissionWarning = TryHardenRootPermissions(root);
        return new GatewayDataPaths(root);
    }

    /// <summary>
    /// Why the data root could not be locked down, if it could not. Logged once the host is built —
    /// this runs before logging is configured, so it cannot report itself.
    /// </summary>
    public static string? PermissionWarning { get; private set; }

    /// <summary>
    /// Best effort, never fatal. Tightening the ACL needs WRITE_DAC, which the Windows "Modify" right
    /// does NOT include — so an administrator who pre-creates the folder and grants the service account
    /// Modify (exactly what the README tells them to do) leaves it unable to rewrite the ACL. Throwing
    /// here killed the process inside Program's first statement, before Serilog existed, so the gateway
    /// vanished without a single line on disk. A gateway that comes up with a loose ACL and says so is
    /// far better than one that will not come up at all on an unattended IPC.
    /// </summary>
    private static string? TryHardenRootPermissions(string root)
    {
        try
        {
            HardenRootPermissions(root);
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not restrict permissions on the data root '{root}': {ex.Message}. " +
                   "The gateway is running, but configuration and audit files may be readable by other " +
                   "accounts on this machine. Grant the service account Full control of that folder.";
        }
    }

    public string Resolve(string? configured, string fallback) => string.IsNullOrWhiteSpace(configured)
        ? fallback
        : Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(Root, configured));

    private static void HardenRootPermissions(string root)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return;
        }
        HardenWindowsAcl(root);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void HardenWindowsAcl(string root)
    {
        var owner = System.Security.Principal.WindowsIdentity.GetCurrent().User
                    ?? throw new InvalidOperationException("The gateway service account has no Windows SID.");
        var acl = new System.Security.AccessControl.DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        Add(owner);
        Add(new System.Security.Principal.SecurityIdentifier(
            System.Security.Principal.WellKnownSidType.LocalSystemSid, null));
        Add(new System.Security.Principal.SecurityIdentifier(
            System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null));
        new DirectoryInfo(root).SetAccessControl(acl);
        return;

        void Add(System.Security.Principal.IdentityReference identity) => acl.AddAccessRule(
            new System.Security.AccessControl.FileSystemAccessRule(identity,
                System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit |
                System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Allow));
    }
}
