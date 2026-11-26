[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Executable,
    [Parameter(Mandatory)][string] $Icon
)

$ErrorActionPreference = 'Stop'
if (-not ('LinkwayLauncherIcon' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

public static class LinkwayLauncherIcon
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr BeginUpdateResource(string path, bool deleteExisting);

    [DllImport("kernel32.dll", EntryPoint = "UpdateResourceW", SetLastError = true)]
    private static extern bool UpdateResource(IntPtr handle, IntPtr type, IntPtr name, ushort language, byte[] bytes, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EndUpdateResource(IntPtr handle, bool discard);

    public static void Apply(string executable, string iconPath)
    {
        byte[] icon = File.ReadAllBytes(iconPath);
        if (icon.Length < 6 || BitConverter.ToUInt16(icon, 0) != 0 || BitConverter.ToUInt16(icon, 2) != 1)
            throw new InvalidDataException("Invalid ICO header.");
        int count = BitConverter.ToUInt16(icon, 4);
        if (count < 1 || count > 32 || icon.Length < 6 + 16 * count)
            throw new InvalidDataException("Invalid ICO directory.");

        // A PE icon group uses resource IDs in place of each image's offset in the ICO.
        var group = new byte[6 + 14 * count];
        Buffer.BlockCopy(icon, 0, group, 0, 6);
        IntPtr update = BeginUpdateResource(executable, false);
        if (update == IntPtr.Zero)
            throw new Win32Exception();
        try
        {
            for (int index = 0; index < count; index++)
            {
                int entry = 6 + 16 * index;
                uint size = BitConverter.ToUInt32(icon, entry + 8);
                uint offset = BitConverter.ToUInt32(icon, entry + 12);
                if (size == 0 || offset < 6 + 16 * count || offset + (ulong)size > (ulong)icon.Length)
                    throw new InvalidDataException("Invalid ICO frame.");
                byte[] frame = new byte[size];
                Buffer.BlockCopy(icon, (int)offset, frame, 0, frame.Length);
                int id = index + 1;
                int groupEntry = 6 + 14 * index;
                Buffer.BlockCopy(icon, entry, group, groupEntry, 12);
                group[groupEntry + 12] = (byte)id;
                if (!UpdateResource(update, (IntPtr)3, (IntPtr)id, 0, frame, size))
                    throw new Win32Exception();
            }
            if (!UpdateResource(update, (IntPtr)14, (IntPtr)1, 0, group, (uint)group.Length))
                throw new Win32Exception();
            bool saved = EndUpdateResource(update, false);
            update = IntPtr.Zero;
            if (!saved)
                throw new Win32Exception();
        }
        finally
        {
            if (update != IntPtr.Zero)
                EndUpdateResource(update, true);
        }
    }
}
'@
}
[LinkwayLauncherIcon]::Apply([IO.Path]::GetFullPath($Executable), [IO.Path]::GetFullPath($Icon))
