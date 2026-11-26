using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

namespace VPNDesktop;

internal static class ApplicationInventory
{
    public static string[] Running()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try { AddExecutable(paths, process.MainModule?.FileName); }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        return Sort(paths);
    }

    public static string[] Installed()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var registry = RegistryKey.OpenBaseKey(hive, view);
            using var entries = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
            if (entries is null)
                continue;
            foreach (string name in entries.GetSubKeyNames())
            {
                using var entry = entries.OpenSubKey(name);
                AddExecutable(paths, entry?.GetValue(null) as string);
            }
        }

        foreach (var folder in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms })
        {
            string root = Environment.GetFolderPath(folder);
            if (!Directory.Exists(root))
                continue;
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint, MaxRecursionDepth = 12 };
            foreach (string shortcut in Directory.EnumerateFiles(root, "*.lnk", options))
                AddExecutable(paths, ShortcutTarget(shortcut));
        }
        return Sort(paths);
    }

    private static string? ShortcutTarget(string path)
    {
        object? link = null;
        try
        {
            link = new ShellLink();
            ((IPersistFile)link).Load(path, 0);
            var target = new StringBuilder(32768);
            int code = ((IShellLink)link).GetPath(target, target.Capacity, IntPtr.Zero, 4);
            return code == 0 ? target.ToString() : null;
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException) { return null; }
        finally
        {
            if (link is not null)
                Marshal.FinalReleaseComObject(link);
        }
    }

    private static void AddExecutable(HashSet<string> paths, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            return;
        candidate = Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'));
        try
        {
            if (!Path.IsPathFullyQualified(candidate) ||
                !Path.GetExtension(candidate).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                return;
            // Registry entries and shortcuts can refer to an unavailable network share.
            if (!candidate.StartsWith(@"\\", StringComparison.Ordinal) && File.Exists(candidate))
                paths.Add(Path.GetFullPath(candidate));
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException) { }
    }

    private static string[] Sort(HashSet<string> paths) => paths.OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLink
    {
        [PreserveSig]
        int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int maxPath, IntPtr findData, uint flags);
    }
}

