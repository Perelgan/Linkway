using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace VPNDesktop;

internal sealed class ApplicationPickerEntry(string path) : INotifyPropertyChanged
{
    public string Path { get; } = path;
    public string Name { get; } = System.IO.Path.GetFileName(path);
    public BitmapSource? Icon { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public static async Task LoadIconsAsync(ApplicationPickerEntry[] entries, CancellationToken cancellationToken)
    {
        foreach (var batch in entries.Chunk(12))
        {
            var icons = await Task.Run(() => batch.Select(entry =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ReadIcon(entry.Path);
            }).ToArray(), cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            for (int i = 0; i < batch.Length; i++)
            {
                if (icons[i] is not { } icon)
                    continue;
                batch[i].Icon = icon;
                batch[i].PropertyChanged?.Invoke(batch[i], new(nameof(Icon)));
            }
        }
    }

    internal static BitmapSource? ReadIcon(string path)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            if (ExtractIconExW(path, 0, IntPtr.Zero, out handle, 1) != 1 || handle == IntPtr.Zero)
                return null;
            var icon = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            icon.Freeze();
            return icon;
        }
        catch (Exception error) when (error is ExternalException or ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
        finally
        {
            if (handle != IntPtr.Zero)
                DestroyIcon(handle);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint ExtractIconExW(string file, int index, IntPtr largeIcon, out IntPtr smallIcon, uint count);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
