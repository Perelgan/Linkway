using System.Diagnostics;
using System.IO;

namespace VPNDesktop;

internal static class HostRuntime
{
    private const string HostExecutable = "Linkway.Session.exe";
    private static string Root => HostProtocol.Elevated
        ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        : ProfileStore.DirectoryPath;

    public static bool IsSnapshot(string directory)
    {
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string name = Path.GetFileName(directory);
        return string.Equals(Path.GetDirectoryName(directory), Root, StringComparison.OrdinalIgnoreCase)
            && name.StartsWith(HostProtocol.RuntimePrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(name.AsSpan(HostProtocol.RuntimePrefix.Length), "N", out _);
    }

    public static void Reap()
    {
        if (!Directory.Exists(Root))
            return;
        foreach (string folder in Directory.EnumerateDirectories(Root, HostProtocol.RuntimePrefix + "*"))
        {
            if (!IsSnapshot(folder) || (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                continue;
            try
            {
                using (File.Open(Path.Combine(folder, "session-host.lock"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None)) { }
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    public static async Task EnsureAsync(CancellationToken token)
    {
        if (HostProtocol.Exists())
            return;

        string runtime = Path.Combine(Root, HostProtocol.RuntimePrefix + Guid.NewGuid().ToString("N"));
        string appExecutable = Environment.ProcessPath
            ?? throw new IOException("Не удалось определить путь фонового процесса Linkway.");
        var core = MihomoVersions.Active.Executable;
        await Task.Run(() =>
        {
            Reap();
            Directory.CreateDirectory(runtime);
            foreach (string file in Directory.EnumerateFiles(AppContext.BaseDirectory))
            {
                token.ThrowIfCancellationRequested();
                string name = Path.GetFileName(file);
                if (file.Equals(appExecutable, StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("mihomo.exe", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("wintun.dll", StringComparison.OrdinalIgnoreCase))
                    continue;
                File.Copy(file, Path.Combine(runtime, name));
            }
            File.Copy(appExecutable, Path.Combine(runtime, HostExecutable));
            File.Copy(core, Path.Combine(runtime, "mihomo.exe"));
            string driver = Path.Combine(Path.GetDirectoryName(core)!, "wintun.dll");
            if (File.Exists(driver))
                File.Copy(driver, Path.Combine(runtime, "wintun.dll"));
        }, token);
        token.ThrowIfCancellationRequested();

        var start = new ProcessStartInfo(Path.Combine(runtime, HostExecutable))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = runtime
        };
        start.ArgumentList.Add(HostProtocol.Argument);
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить фоновой VPN.");
        try { await VpnSessionClient.WaitForHostAsync(token); }
        catch
        {
            if (!process.HasExited)
                process.Kill();
            throw;
        }
    }
}
