using System.IO;
using System.Reflection;
using DevBar.Modules.CiPulse;
using DevBar.Modules.Claude;
using DevBar.Modules.ClipboardHistory;
using DevBar.Modules.Docker;
using DevBar.Modules.GitStatus;
using DevBar.Modules.Jarvis;
using DevBar.Modules.Media;
using DevBar.Modules.Ports;
using DevBar.Modules.Shelf;
using DevBar.Sdk;

namespace DevBar.Core;

internal static class ModuleHost
{
    public static string PluginDir => Path.Combine(Config.Dir, "modules");

    public static List<IDevBarModule> Build(Config config, StartupArgs args, IJarvisHost jarvisHost)
    {
        var jarvis = new JarvisModule(config, jarvisHost);
        var all = new List<IDevBarModule>
        {
            jarvis,
            new ClipboardModule(config),
            new ShelfModule(args.ShelfSeed),
            new ClaudeModule(),
            new PortsModule(),
            new DockerModule(),
            new GitStatusModule(config),
            new CiPulseModule(config),
            new MediaModule(),
        };

        all.AddRange(LoadPlugins());

        // apply configured order, then filter disabled
        var ordered = all
            .OrderBy(m =>
            {
                int i = config.ModuleOrder.IndexOf(m.Id);
                return i < 0 ? int.MaxValue : i;
            })
            .Where(m => !config.DisabledModules.Contains(m.Id))
            .ToList();

        jarvis.AttachModules(all);
        return ordered;
    }

    /// <summary>
    /// Community modules: any DLL under %LOCALAPPDATA%\DevBar\modules\ exposing
    /// public IDevBarModule implementations. A broken plugin is skipped, never fatal.
    /// </summary>
    private static IEnumerable<IDevBarModule> LoadPlugins()
    {
        var found = new List<IDevBarModule>();
        try
        {
            if (!Directory.Exists(PluginDir)) return found;
            foreach (var dll in Directory.EnumerateFiles(PluginDir, "*.dll", SearchOption.AllDirectories))
            {
                try
                {
                    var asm = Assembly.LoadFrom(dll);
                    foreach (var type in asm.GetTypes()
                                 .Where(t => !t.IsAbstract && typeof(IDevBarModule).IsAssignableFrom(t)))
                    {
                        if (Activator.CreateInstance(type) is IDevBarModule m)
                            found.Add(m);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Plugin load failed: {dll}: {ex.Message}");
                }
            }
        }
        catch { /* plugin dir unreadable - run with built-ins */ }
        return found;
    }
}
