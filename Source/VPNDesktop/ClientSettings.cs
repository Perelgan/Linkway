using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VPNDesktop;

internal sealed record ClientSettings(bool Tun = false, bool DirectLan = false, bool KillSwitch = false, bool ExperimentalUpdates = false,
    bool LightTheme = false, bool AutoConnect = false, bool AutoRecover = false, bool MinimizeToTray = false, bool SaveHistory = false,
    bool DeveloperMode = false, WindowCloseAction CloseAction = WindowCloseAction.Ask)
{
    private static readonly string PathName = Path.Combine(ProfileStore.DirectoryPath, "settings.json");

    [JsonIgnore]
    public NetworkOptions? Network { get; init; }

    public string? ProtectedNetwork { get; init; }

    public static ClientSettings Load()
    {
        if (!File.Exists(PathName))
            return new();
        var settings = JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(PathName)) ?? new();
        if (settings.ProtectedNetwork is not { } encrypted)
            return settings;
        byte[] clear = ProtectedData.Unprotect(Convert.FromBase64String(encrypted), null, DataProtectionScope.CurrentUser);
        try { return settings with { Network = JsonSerializer.Deserialize<NetworkOptions>(clear), ProtectedNetwork = null }; }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public void Save()
    {
        string? encrypted = null;
        if (Network is not null)
        {
            byte[] clear = JsonSerializer.SerializeToUtf8Bytes(Network);
            try { encrypted = Convert.ToBase64String(ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser)); }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        File.WriteAllText(PathName + ".new", JsonSerializer.Serialize(this with { ProtectedNetwork = encrypted }));
        File.Move(PathName + ".new", PathName, true);
    }
}
