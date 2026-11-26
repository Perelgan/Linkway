using System.IO;
using System.Text.Json;

namespace VPNDesktop;

internal sealed record ClientSettings(bool Tun = false, bool DirectLan = false, string? Repository = null, bool KillSwitch = false)
{
    private static readonly string PathName = Path.Combine(ProfileStore.DirectoryPath, "settings.json");

    public static ClientSettings Load() => File.Exists(PathName)
        ? JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(PathName)) ?? new()
        : new();

    public void Save()
    {
        File.WriteAllText(PathName + ".new", JsonSerializer.Serialize(this));
        File.Move(PathName + ".new", PathName, true);
    }
}
