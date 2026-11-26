using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace VPNDesktop;

internal static class WindowsProxy
{
    private const int InternetOptionPerConnection = 75;
    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;
    private const int Flags = 1;
    private const int Server = 2;
    private const int Bypass = 3;
    private const int AutoConfig = 4;
    private const int UiFlags = 10;
    private const int DirectAndManual = 3;
    private static readonly int OptionSize = Marshal.SizeOf<ConnectionOption>();
    private static readonly string JournalPath = Path.Combine(ProfileStore.DirectoryPath, "proxy-restore.json");

    private sealed record Settings(int Flags, string Server, string Bypass, string AutoConfig);
    private sealed record Journal(Settings Before, Settings Applied);

    public static void Enable(int port)
    {
        Restore();
        var target = new Settings(DirectAndManual, $"127.0.0.1:{port}", "", "");
        var journal = new Journal(Read(), target);
        File.WriteAllText(JournalPath + ".new", JsonSerializer.Serialize(journal));
        File.Move(JournalPath + ".new", JournalPath, true);
        Write(target);
        if (Read() != target)
            throw new IOException("Windows не приняла настройки системного прокси.");
    }

    public static bool UsesLinkway(int port)
    {
        var current = Read();
        return (current.Flags & 2) != 0 && current.Server == $"127.0.0.1:{port}";
    }

    public static void Restore()
    {
        if (!File.Exists(JournalPath))
            return;

        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(JournalPath))
            ?? throw new IOException("Не удалось прочитать резервную копию настроек прокси.");
        var current = Read();
        // Only replace settings still pointing at this session's listener.
        if (current.Server == journal.Applied.Server && (current.Flags & 2) != 0)
            Write(journal.Before);
        File.Delete(JournalPath);
    }

    private static Settings Read()
    {
        try { return Read(UiFlags); }
        catch (Win32Exception) { return Read(Flags); }
    }

    private static Settings Read(int flagOption)
    {
        int[] keys = [flagOption, Server, Bypass, AutoConfig];
        IntPtr options = Marshal.AllocHGlobal(OptionSize * keys.Length);
        try
        {
            for (int i = 0; i < keys.Length; i++)
                Marshal.StructureToPtr(new ConnectionOption { Option = keys[i] }, options + i * OptionSize, false);
            var list = MakeList(options, keys.Length);
            int size = list.Size;
            if (!InternetQueryOption(IntPtr.Zero, InternetOptionPerConnection, ref list, ref size))
                throw new Win32Exception();

            return new Settings(
                Marshal.PtrToStructure<ConnectionOption>(options).Value.ToInt32(),
                ReadText(1), ReadText(2), ReadText(3));
        }
        finally
        {
            // WinINet returns string buffers allocated with GlobalAlloc.
            for (int i = 1; i < keys.Length; i++)
            {
                var value = Marshal.PtrToStructure<ConnectionOption>(options + i * OptionSize).Value;
                if (value != IntPtr.Zero)
                    GlobalFree(value);
            }
            Marshal.FreeHGlobal(options);
        }

        string ReadText(int index)
        {
            var value = Marshal.PtrToStructure<ConnectionOption>(options + index * OptionSize).Value;
            return Marshal.PtrToStringUni(value) ?? "";
        }
    }

    private static void Write(Settings settings)
    {
        (int Key, string Text)[] strings =
            [(Server, settings.Server), (Bypass, settings.Bypass), (AutoConfig, settings.AutoConfig)];
        var values = new IntPtr[strings.Length];
        IntPtr options = Marshal.AllocHGlobal(OptionSize * (strings.Length + 1));
        try
        {
            Marshal.StructureToPtr(new ConnectionOption { Option = Flags, Value = new IntPtr(settings.Flags) },
                options, false);
            for (int i = 0; i < strings.Length; i++)
            {
                values[i] = Marshal.StringToHGlobalUni(strings[i].Text);
                Marshal.StructureToPtr(new ConnectionOption { Option = strings[i].Key, Value = values[i] },
                    options + (i + 1) * OptionSize, false);
            }
            var list = MakeList(options, strings.Length + 1);
            if (!InternetSetConnectionOption(IntPtr.Zero, InternetOptionPerConnection, ref list, list.Size))
                throw new Win32Exception();

            InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
        }
        finally
        {
            foreach (var value in values)
                Marshal.FreeHGlobal(value);
            Marshal.FreeHGlobal(options);
        }
    }

    private static OptionList MakeList(IntPtr options, int count) => new()
    {
        Size = Marshal.SizeOf<OptionList>(), Count = count, Options = options
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct ConnectionOption
    {
        public int Option;
        public IntPtr Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OptionList
    {
        public int Size;
        public IntPtr Connection;
        public int Count, OptionError;
        public IntPtr Options;
    }

    [DllImport("wininet.dll", EntryPoint = "InternetQueryOptionW", SetLastError = true)]
    private static extern bool InternetQueryOption(IntPtr internet, int option, ref OptionList list, ref int size);

    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    private static extern bool InternetSetConnectionOption(IntPtr internet, int option, ref OptionList list, int size);

    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr internet, int option, IntPtr buffer, int size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr buffer);
}
