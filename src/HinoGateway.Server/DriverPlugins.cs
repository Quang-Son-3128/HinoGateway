using System.Reflection;
using System.Runtime.Loader;
using HinoGateway.Core;

namespace HinoGateway.Server;

/// <summary>
/// Loads driver factories from assemblies dropped into a <c>drivers/</c> folder beside the executable.
///
/// <para>
/// Adding a protocol used to mean editing <c>Program.cs</c> and rebuilding the gateway, which put
/// every new driver behind a full release of everything else. The scope here is deliberately the
/// narrow half of that problem: discovery and a version check. There is no signing and no process
/// isolation, because this is a gateway deployed to a fixed set of industrial PCs by the team that
/// builds it — a driver in this folder is code the operator put there, with the same trust as the
/// gateway binary it sits next to. Anyone proposing to accept drivers from elsewhere would need both
/// of those, and should say so rather than inherit this.
/// </para>
///
/// <para>
/// Every failure here is survivable: a folder that does not exist, an assembly that will not load, a
/// factory that throws in its constructor. A gateway that refuses to start because one optional
/// driver is broken has turned an inconvenience into an outage.
/// </para>
/// </summary>
public static class DriverPlugins
{
    /// <summary>
    /// The driver contract this gateway implements. A plugin declares the version it was built
    /// against with <see cref="DriverContractAttribute"/>; a mismatch in the major number is refused
    /// rather than loaded and hoped for, because the failure it produces otherwise is a
    /// <c>MissingMethodException</c> at the first poll of a live plant.
    /// </summary>
    public const int ContractVersion = 1;

    public const string FolderName = "drivers";

    public sealed record LoadResult(IReadOnlyList<IDeviceDriverFactory> Factories, IReadOnlyList<string> Problems);

    /// <summary>Scan <paramref name="folder"/> (default: <c>drivers/</c> beside the executable).</summary>
    public static LoadResult Load(string? folder = null)
    {
        var directory = folder ?? Path.Combine(AppContext.BaseDirectory, FolderName);
        var factories = new List<IDeviceDriverFactory>();
        var problems = new List<string>();

        if (!Directory.Exists(directory))
            return new LoadResult(factories, problems);

        foreach (var file in Directory.EnumerateFiles(directory, "*.dll").OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                LoadAssembly(file, factories, problems);
            }
            catch (Exception ex)
            {
                problems.Add($"'{Path.GetFileName(file)}' could not be loaded: {ex.Message}");
            }
        }

        return new LoadResult(factories, problems);
    }

    private static void LoadAssembly(string file, List<IDeviceDriverFactory> factories, List<string> problems)
    {
        // A resolver rooted at the plugin, so a driver may ship its own protocol library without the
        // gateway having to reference it. Types from the shared contract still resolve to the ones
        // already loaded, which is what makes the cast to IDeviceDriverFactory work at all.
        var context = new PluginLoadContext(file);
        var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(file));

        var declared = assembly.GetCustomAttribute<DriverContractAttribute>();
        if (declared is null)
        {
            problems.Add(
                $"'{Path.GetFileName(file)}' does not declare [assembly: DriverContract(...)], so there is no " +
                "way to tell which version of the driver contract it was built against. It was not loaded.");
            return;
        }

        if (declared.Version != ContractVersion)
        {
            problems.Add(
                $"'{Path.GetFileName(file)}' was built against driver contract version {declared.Version}; " +
                $"this gateway implements version {ContractVersion}. It was not loaded.");
            return;
        }

        var found = 0;
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface || !typeof(IDeviceDriverFactory).IsAssignableFrom(type))
                continue;
            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                problems.Add($"'{type.FullName}' has no parameterless constructor and was skipped.");
                continue;
            }

            try
            {
                factories.Add((IDeviceDriverFactory)Activator.CreateInstance(type)!);
                found++;
            }
            catch (Exception ex)
            {
                problems.Add($"'{type.FullName}' could not be constructed: {ex.Message}");
            }
        }

        if (found == 0)
            problems.Add($"'{Path.GetFileName(file)}' contains no usable {nameof(IDeviceDriverFactory)}.");
    }

    private sealed class PluginLoadContext(string pluginPath) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(pluginPath);

        protected override Assembly? Load(AssemblyName name)
        {
            // Returning null hands the request to the default context, which is what keeps
            // HinoGateway.Core a single shared identity. Loading a second copy of it would make every
            // IDeviceDriverFactory in the plugin a different type from the one the engine expects,
            // and the driver would simply never be found.
            if (name.Name?.StartsWith("HinoGateway.", StringComparison.Ordinal) == true)
                return null;

            var path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override nint LoadUnmanagedDll(string name)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(name);
            return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}

/// <summary>
/// Declares which version of the driver contract an assembly was built against:
/// <c>[assembly: DriverContract(DriverPlugins.ContractVersion)]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class DriverContractAttribute(int version) : Attribute
{
    public int Version { get; } = version;
}
