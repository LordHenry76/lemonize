using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace Lemonize.Interop;

[Flags]
public enum DragModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4,
}

/// <summary>
/// Watches the global mouse to notice when the user is dragging something while holding Shift, Alt or Ctrl.
/// It cannot see what is being dragged; the wheel window confirms that via OLE DragEnter.
/// </summary>
/// <remarks>
/// The hook lives on its own thread with its own message loop. Windows silently uninstalls a
/// low-level hook whose callback is late (LowLevelHooksTimeout), so it must never share a thread
/// with UI work such as loading thumbnails or image libraries.
/// </remarks>
public sealed class MouseHook : IDisposable
{
    private const double DragThreshold = 10;

    private readonly Native.LowLevelMouseProc _proc;
    private readonly Dispatcher _ui;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;

    // Only touched on the hook thread.
    private Point? _downAt;
    private bool _dragging;
    private bool _signaled;
    private bool _seenEvent;

    /// <summary>Raised (on the UI thread) once per drag when a modifier is held, with the cursor in screen pixels.</summary>
    public event Action<Point, DragModifiers>? ModifiedDrag;

    /// <summary>Raised (on the UI thread) when the left button is released after a drag.</summary>
    public event Action? ButtonReleased;

    public MouseHook()
    {
        _proc = Callback;
        _ui = Dispatcher.CurrentDispatcher;
    }

    public void Start()
    {
        if (_thread is not null) return;
        using var ready = new ManualResetEventSlim();
        Exception? failure = null;
        _thread = new Thread(() =>
        {
            _threadId = Native.GetCurrentThreadId();
            _hook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, Native.GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) failure = new InvalidOperationException("Could not install mouse hook: " + Marshal.GetLastWin32Error());
            Lemonize.Core.Diag.Log($"hook: installed={_hook != IntPtr.Zero} thread={_threadId}");
            ready.Set();
            if (_hook == IntPtr.Zero) return;

            while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
            Native.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
            Lemonize.Core.Diag.Log("hook: removed, thread exiting");
        })
        {
            IsBackground = true,
            Name = "Lemonize mouse hook",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
        ready.Wait();
        if (failure is not null)
        {
            _thread = null;
            throw failure;
        }
    }

    public void Stop()
    {
        if (_thread is null) return;
        Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
        _thread = null;
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (!_seenEvent)
        {
            _seenEvent = true;
            Lemonize.Core.Diag.Log("hook: first mouse event received");
        }
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            var point = new Point(data.pt.X, data.pt.Y);
            switch ((int)wParam)
            {
                case Native.WM_LBUTTONDOWN:
                    _downAt = point;
                    _dragging = false;
                    _signaled = false;
                    break;
                case Native.WM_LBUTTONUP:
                    var wasDragging = _dragging;
                    _downAt = null;
                    _dragging = false;
                    _signaled = false;
                    if (wasDragging) _ui.BeginInvoke(() => ButtonReleased?.Invoke());
                    break;
                case Native.WM_MOUSEMOVE when _downAt is { } start:
                    if (!_dragging && (Math.Abs(point.X - start.X) > DragThreshold || Math.Abs(point.Y - start.Y) > DragThreshold))
                        _dragging = true;
                    if (_dragging && !_signaled)
                    {
                        var mods = CurrentModifiers();
                        if (mods != DragModifiers.None)
                        {
                            _signaled = true;
                            Lemonize.Core.Diag.Log($"hook: drag with {mods} at {point}");
                            _ui.BeginInvoke(() => ModifiedDrag?.Invoke(point, mods));
                        }
                    }
                    break;
            }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public static DragModifiers CurrentModifiers()
    {
        var mods = DragModifiers.None;
        if (Native.IsKeyDown(Native.VK_SHIFT)) mods |= DragModifiers.Shift;
        if (Native.IsKeyDown(Native.VK_CONTROL)) mods |= DragModifiers.Control;
        if (Native.IsKeyDown(Native.VK_MENU)) mods |= DragModifiers.Alt;
        return mods;
    }

    public void Dispose() => Stop();
}
