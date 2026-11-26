using System.IO;
using System.Net.Http.Headers;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VPNDesktop;

internal sealed class SubscriptionDeviceException(string message) : IOException(message);

internal static partial class SubscriptionDevice
{
    private const string Unsupported = "Провайдер не принял идентификатор устройства Linkway. Проверьте разрешённые клиенты в кабинете.";
    private const string LimitReached = "Провайдер сообщил, что лимит устройств исчерпан. Проверьте список устройств в кабинете.";
    private static readonly Lazy<Guid?> WindowsInstallation = new(ReadWindowsInstallation);

    public static void AddHeaders(Uri address, HttpRequestHeaders headers)
    {
        if (address.Scheme != Uri.UriSchemeHttps || WindowsInstallation.Value is not { } installation)
            return;
        headers.Add("x-hwid", MakeIdentifier(installation, address.IdnHost));
        headers.Add("x-device-os", "Windows");
        headers.Add("x-ver-os", Environment.OSVersion.Version.ToString());
    }

    internal static string MakeIdentifier(Guid installation, string host)
    {
        // Scope the stable identifier to the subscription host. The Windows installation ID stays local.
        byte[] value = Encoding.UTF8.GetBytes($"VPNDesktop/HWID/v1/{host.ToLowerInvariant()}/{installation:N}");
        return Convert.ToHexString(SHA256.HashData(value));
    }

    private static Guid? ReadWindowsInstallation()
    {
        try
        {
            using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = registry.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return Guid.TryParse(key?.GetValue("MachineGuid") as string, out var id) && id != Guid.Empty ? id : null;
        }
        catch (Exception error) when (error is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public static void CheckHeaders(HttpResponseHeaders headers)
    {
        if (Enabled("x-hwid-max-devices-reached") || Enabled("x-hwid-limit"))
            throw new SubscriptionDeviceException(LimitReached);
        if (Enabled("x-hwid-not-supported"))
            throw new SubscriptionDeviceException(Unsupported);

        bool Enabled(string key) => headers.TryGetValues(key, out var values) &&
            values.Any(value => value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) || value.Trim() == "1");
    }

    public static string? RejectionForNode(string name)
    {
        string label = LeadingSymbols().Replace(name.Trim(), "").ToUpperInvariant();
        return label switch
        {
            "HWID ERROR" or "APP NOT SUPPORTED" => Unsupported,
            "HWID LIMIT" or "HWID LIMIT REACHED" or "MAX DEVICES REACHED" or "DEVICE LIMIT REACHED" => LimitReached,
            _ => null
        };
    }

    [GeneratedRegex(@"\A[^\p{L}\p{N}]+")]
    private static partial Regex LeadingSymbols();
}
