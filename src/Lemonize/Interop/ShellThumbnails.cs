using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ImageMagick;
using Lemonize.Core;
using Lemonize.Engines;

namespace Lemonize.Interop;

/// <summary>
/// File previews for the wheel, the progress list and floating files.
/// </summary>
/// <remarks>
/// Deliberately does NOT use Explorer's thumbnail handlers (IShellItemImageFactory): they load
/// third-party components into this process (e.g. Adobe's PDF handler, Media Foundation for video)
/// which repeatedly left Lemonize hanging at exit. Previews are rendered with our own engines;
/// everything else gets the file type icon, looked up by extension only.
/// </remarks>
public static class ShellThumbnails
{
    public static Task<BitmapSource?> GetAsync(string path, int size) => Task.Run(() => Get(path, size));

    /// <summary>A preview of the content when we can render one, otherwise the file type icon. May be slow.</summary>
    public static BitmapSource? Get(string path, int size)
    {
        try
        {
            var preview = FileKinds.Detect(path) switch
            {
                FileKind.Image or FileKind.AnimatedImage => ImagePreview(path, size),
                FileKind.Pdf => FromPng(PdfEngine.RenderAsync(path, [0], (uint)(size * 2)).GetAwaiter().GetResult().FirstOrDefault(), size),
                FileKind.Video when ToolLocator.Ffmpeg is not null => FromPng(Ffmpeg.FrameAsync(path, 1, size * 2).GetAwaiter().GetResult(), size),
                _ => null,
            };
            if (preview is not null) return preview;
        }
        catch (Exception)
        {
            // Unreadable or unusual file: fall back to the icon.
        }
        return Icon(path);
    }

    private static BitmapSource ImagePreview(string path, int size)
    {
        var settings = ImageEngine.ReadSettingsFor(path);
        settings.FrameIndex = 0;
        settings.FrameCount = 1;
        // Let the JPEG decoder downscale while reading: much faster for camera photos.
        settings.SetDefine(MagickFormat.Jpeg, "size", $"{size * 2}x{size * 2}");
        using var image = new MagickImage(path, settings);
        image.AutoOrient();
        image.Thumbnail(new MagickGeometry((uint)(size * 2), (uint)(size * 2)));
        return ImageEngine.ToBitmapSource(image);
    }

    private static BitmapSource? FromPng(byte[]? png, int size)
    {
        if (png is null || png.Length == 0) return null;
        using var image = new MagickImage(png);
        image.Thumbnail(new MagickGeometry((uint)(size * 2), (uint)(size * 2)));
        return ImageEngine.ToBitmapSource(image);
    }

    // ---------- File type icon (by extension, no shell extensions instantiated) ----------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

    public static BitmapSource? Icon(string path)
    {
        var isFolder = Directory.Exists(path);
        // USEFILEATTRIBUTES: resolve by name/extension only, without opening the file.
        var probe = isFolder ? "folder" : "file" + Path.GetExtension(path);
        var info = new SHFILEINFO();
        var result = SHGetFileInfo(probe, isFolder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL, ref info,
            (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }
}
