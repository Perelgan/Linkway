using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace VPNDesktop;

internal static class Bootstrap
{
    [STAThread]
    private static void Main(string[] args)
    {
        AssemblyLoadContext.Default.Resolving += ResolveLanguageAssembly;
        if (args is [HostProtocol.Argument])
        {
            if (HostRuntime.IsSnapshot(AppContext.BaseDirectory))
                VpnSessionHost.Run();
            return;
        }
        RunApplication();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunApplication()
    {
        new App().Run();
    }

    private static Assembly? ResolveLanguageAssembly(AssemblyLoadContext context, AssemblyName request)
    {
        if (request.Name is not { } name || !name.EndsWith(".resources", StringComparison.Ordinal)
            || Path.GetFileName(name) != name || string.IsNullOrEmpty(request.CultureName))
            return null;

        string culture = CultureInfo.GetCultureInfo(request.CultureName).Name;
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Languages", culture, name + ".dll"));
        return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
    }
}
