using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Xml.Linq;

namespace VPNDesktop;

internal static class WindowsVpnStartup
{
    public const string LaunchArgument = "--windows-vpn-startup";
    public const string ConfigureArgument = "--configure-windows-vpn-startup";
    private const int TaskNotFound = unchecked((int)0x80070002);
    private const int TaskPathNotFound = unchecked((int)0x80070003);
    private static readonly XNamespace Schema = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    internal static string Launcher => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "VPNDesktop.exe"));

    public static bool IsEnabled()
    {
        using var identity = WindowsIdentity.GetCurrent();
        string sid = identity.User!.Value;
        var task = FindTask(Connect(), sid);
        return task is not null && (bool)task.Enabled && RunsThisInstallation((string)task.Xml);
    }

    public static async Task SetAsync(bool enabled, CancellationToken token)
    {
        using var identity = WindowsIdentity.GetCurrent();
        string sid = identity.User!.Value;
        if (HostProtocol.Elevated)
        {
            Configure(sid, enabled);
            return;
        }

        var launch = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        launch.ArgumentList.Add(ConfigureArgument);
        launch.ArgumentList.Add(sid);
        launch.ArgumentList.Add(enabled ? "enable" : "disable");
        try
        {
            using var process = Process.Start(launch) ?? throw new IOException("Не удалось открыть настройку автозапуска VPN.");
            await process.WaitForExitAsync(token);
            if (process.ExitCode != 0)
                throw new IOException("Не удалось настроить автозапуск VPN. Подтвердите права администратора для этой учётной записи.");
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            throw new IOException("Подтверждение Windows отменено. Настройка VPN при входе не изменилась.", error);
        }
    }

    internal static void Configure(string sid, bool enabled)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!HostProtocol.Elevated || identity.User!.Value != sid)
            throw new UnauthorizedAccessException();

        dynamic folder = Connect();
        if (enabled)
        {
            if (!File.Exists(Launcher))
                throw new IOException("Рядом с папкой Libraries отсутствует VPNDesktop.exe.");
            const int CreateOrUpdate = 6, InteractiveToken = 3;
            folder.RegisterTask(TaskName(sid), TaskXml(Launcher, sid), CreateOrUpdate, sid, null, InteractiveToken, null);
        }
        else
        {
            var task = FindTask(folder, sid);
            if (task is not null && RunsThisInstallation((string)task.Xml))
                folder.DeleteTask(TaskName(sid), 0);
        }
    }

    internal static string TaskXml(string launcher, string sid) => new XElement(Schema + "Task",
        new XAttribute("version", "1.2"),
        Element("Triggers", Element("LogonTrigger", Element("UserId", sid))),
        Element("Principals", Element("Principal", new XAttribute("id", "Linkway"),
            Element("UserId", sid), Element("LogonType", "InteractiveToken"), Element("RunLevel", "HighestAvailable"))),
        Element("Settings", Element("MultipleInstancesPolicy", "IgnoreNew"),
            Element("DisallowStartIfOnBatteries", false), Element("StopIfGoingOnBatteries", false),
            Element("ExecutionTimeLimit", "PT0S")),
        Element("Actions", new XAttribute("Context", "Linkway"), Element("Exec",
            Element("Command", launcher), Element("Arguments", LaunchArgument),
            Element("WorkingDirectory", Path.GetDirectoryName(launcher))))).ToString();

    private static XElement Element(string name, params object?[] content) => new(Schema + name, content);

    private static bool RunsThisInstallation(string xml)
    {
        var action = XElement.Parse(xml).Element(Schema + "Actions")?.Element(Schema + "Exec");
        return string.Equals((string?)action?.Element(Schema + "Command"), Launcher, StringComparison.OrdinalIgnoreCase) &&
            (string?)action?.Element(Schema + "Arguments") == LaunchArgument;
    }

    private static dynamic Connect()
    {
        dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
        scheduler.Connect();
        return scheduler.GetFolder(@"\");
    }

    private static dynamic? FindTask(dynamic folder, string sid)
    {
        try { return folder.GetTask(TaskName(sid)); }
        catch (Exception error) when (error.HResult is TaskNotFound or TaskPathNotFound) { return null; }
    }

    private static string TaskName(string sid) => $"Linkway VPN at sign-in ({sid})";
}
