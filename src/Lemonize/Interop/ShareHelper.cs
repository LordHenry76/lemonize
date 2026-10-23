using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Lemonize.Interop;

/// <summary>Opens the Windows share sheet (Nearby Sharing, Mail, apps…) for files.</summary>
public static class ShareHelper
{
    [ComImport, Guid("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataTransferManagerInterop
    {
        IntPtr GetForWindow([In] IntPtr appWindow, [In] ref Guid riid);
        void ShowShareUIForWindow(IntPtr appWindow);
    }

    // IID of Windows.ApplicationModel.DataTransfer.DataTransferManager
    private static Guid _dtmIid = new("a5caee9b-8708-49d1-8d36-67d25a8da00c");

    private static Window? _host;

    public static void Share(IReadOnlyList<string> paths)
    {
        // The share sheet anchors to a window; a tiny invisible one near the cursor works for a tray app.
        _host?.Close();
        var cursor = Native.CursorPixels();
        var scale = Native.ScaleAt(cursor);
        var host = new Window
        {
            Width = 1, Height = 1, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
            AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent,
            Left = cursor.X / scale, Top = cursor.Y / scale, Topmost = true,
        };
        host.Show();
        _host = host;
        var hwnd = new WindowInteropHelper(host).Handle;

        var interop = DataTransferManager.As<IDataTransferManagerInterop>();
        var ptr = interop.GetForWindow(hwnd, ref _dtmIid);
        var manager = WinRT.MarshalInterface<DataTransferManager>.FromAbi(ptr);

        manager.DataRequested += async (_, args) =>
        {
            var deferral = args.Request.GetDeferral();
            try
            {
                var items = new List<IStorageItem>();
                foreach (var path in paths)
                {
                    items.Add(Directory.Exists(path)
                        ? await StorageFolder.GetFolderFromPathAsync(path)
                        : await StorageFile.GetFileFromPathAsync(path));
                }
                args.Request.Data.Properties.Title = paths.Count == 1 ? Path.GetFileName(paths[0]) : $"{paths.Count} items";
                args.Request.Data.SetStorageItems(items);
            }
            catch (Exception ex)
            {
                args.Request.FailWithDisplayText(ex.Message);
            }
            finally
            {
                deferral.Complete();
            }
        };
        manager.TargetApplicationChosen += (_, _) => host.Dispatcher.BeginInvoke(host.Close);
        interop.ShowShareUIForWindow(hwnd);

        // Close the anchor eventually even if the user dismisses the sheet.
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            host.Close();
        };
        timer.Start();
    }
}
