using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lemonize.Core;
using Lemonize.Interop;

namespace Lemonize.UI;

/// <summary>
/// The ring of choices around the pointer. In drag mode it is an OLE drop target that stays
/// invisible until a file drag actually enters it; in click mode it behaves like a popup menu.
/// </summary>
public sealed class WheelWindow : Window
{
    private const double WheelSize = 780;
    private const double CenterRadius = 62;
    private const double PithWidth = 5;
    private const double RindWidth = 15;
    private static readonly (double Inner, double Outer, int Capacity)[] Rings = [(68, 168, 8), (172, 264, 12), (268, 356, 18)];
    private static readonly WheelMode[] Modes = [WheelMode.Convert, WheelMode.Tools, WheelMode.Actions];

    private readonly WheelCanvas _canvas;
    private readonly ScaleTransform _zoom = new(1, 1, WheelSize / 2, WheelSize / 2);
    private readonly DispatcherTimer _releaseTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private Point _placedAt;
    private double _scale = 1;
    private IReadOnlyList<string> _files = [];
    private bool _clickMode;
    private bool _dropped;

    public WheelWindow()
    {
        Width = Height = WheelSize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        AllowDrop = true;
        Title = "Lemonize";
        _canvas = new WheelCanvas(this) { RenderTransform = _zoom };
        Content = _canvas;
        _releaseTimer.Tick += (_, _) =>
        {
            _releaseTimer.Stop();
            if (!_clickMode) Dismiss();
        };
        Deactivated += (_, _) =>
        {
            if (_clickMode) Dismiss();
        };
        SourceInitialized += (_, _) => Native.MakeToolWindow(this, noActivate: true);
    }

    public WheelMode Mode { get; private set; } = WheelMode.Convert;
    public bool Revealed { get; private set; }
    public IReadOnlyList<string> Files => _files;
    internal List<Sector> Sectors { get; } = [];
    internal int Hover { get; private set; } = -1;
    internal int HoverMode { get; private set; } = -1;
    internal BitmapSource? Thumbnail { get; private set; }
    internal bool ThumbnailPending { get; private set; }
    internal bool ClickMode => _clickMode;

    // ---------- Entry points ----------

    /// <summary>Called by the mouse hook: place an invisible drop target under the pointer.</summary>
    public void Arm(Point pixels, DragModifiers modifiers)
    {
        Diag.Log($"wheel: arm at {pixels} mods={modifiers} visible={IsVisible} revealed={Revealed}");
        if (IsVisible) return;
        _clickMode = false;
        _dropped = false;
        _files = [];
        Revealed = false;
        Mode = ModeFor(modifiers) ?? WheelMode.Convert;
        Sectors.Clear();
        Hover = HoverMode = -1;
        _canvas.InvalidateVisual();
        PlaceAt(pixels);
    }

    /// <summary>Opens the wheel as a clickable menu (main window drops, Explorer context menu).</summary>
    public void ShowFor(IReadOnlyList<string> files, Point pixels, WheelMode mode = WheelMode.Convert)
    {
        Diag.Log($"wheel: show for {files.Count} file(s) (click mode)");
        if (files.Count == 0) return;
        _clickMode = true;
        _dropped = false;
        _files = files;
        Mode = mode;
        PlaceAt(pixels);
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE) & ~Native.WS_EX_NOACTIVATE;
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, (IntPtr)style);
        Reveal();
        Activate();
        Focus();
    }

    public void OnMouseReleased()
    {
        Diag.Log($"wheel: button released visible={IsVisible} clickMode={_clickMode}");
        if (!IsVisible || _clickMode) return;
        // Drop arrives right after button-up when the files landed on us; otherwise the drag ended elsewhere.
        _releaseTimer.Stop();
        _releaseTimer.Start();
    }

    public void Dismiss([System.Runtime.CompilerServices.CallerMemberName] string? caller = null)
    {
        _releaseTimer.Stop();
        Diag.Log($"wheel: dismiss from {caller} visible={IsVisible}");
        if (!IsVisible) return;
        if (_clickMode)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = (long)Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE) | Native.WS_EX_NOACTIVATE;
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, (IntPtr)style);
        }
        Hide();
        Revealed = false;
        _clickMode = false;
        _files = [];
        Thumbnail = null;
    }

    private void PlaceAt(Point pixels)
    {
        var scale = Native.ScaleAt(pixels);
        _placedAt = pixels;
        _scale = scale;
        if (!IsVisible) Show();
        var hwnd = new WindowInteropHelper(this).Handle;
        var px = (int)Math.Round(WheelSize * scale);
        Diag.Log($"wheel: place at {pixels} scale={scale} size={px}px hwnd={hwnd}");
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, (int)(pixels.X - px / 2.0), (int)(pixels.Y - px / 2.0), px, px,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }

    private void Reveal()
    {
        // Thumbnails can take a while (video, large PDFs); keep this thread free and fill it in later.
        Thumbnail = null;
        ThumbnailPending = true;
        var file = _files[0];
        _ = ShellThumbnails.GetAsync(file, 96).ContinueWith(t =>
        {
            if (!Revealed || _files.Count == 0 || _files[0] != file) return;
            Thumbnail = t.Result;
            ThumbnailPending = false;
            _canvas.InvalidateVisual();
        }, TaskScheduler.FromCurrentSynchronizationContext());
        Rebuild();
        Revealed = true;
        _canvas.InvalidateVisual();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var grow = new DoubleAnimation(0.86, 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease };
        _zoom.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        _zoom.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        // Never start from fully transparent: OLE would stop "seeing" the window and report a DragLeave.
        _canvas.BeginAnimation(OpacityProperty, new DoubleAnimation(0.45, 0.9, TimeSpan.FromMilliseconds(110)));
    }

    private void SetMode(WheelMode mode)
    {
        if (mode == Mode) return;
        Mode = mode;
        Rebuild();
        _canvas.InvalidateVisual();
    }

    private void Rebuild()
    {
        Sectors.Clear();
        Hover = -1;
        var items = Commands.Items(Mode, _files);
        var index = 0;
        foreach (var (inner, outer, capacity) in Rings)
        {
            var remaining = items.Count - index;
            if (remaining <= 0) break;
            var count = Math.Min(capacity, remaining);
            // Balance the last two rings so the outer one isn't a lonely sliver.
            var sweep = 360.0 / count;
            for (var i = 0; i < count; i++)
            {
                var start = -90 - sweep / 2 + i * sweep;
                Sectors.Add(new Sector(items[index++], inner, outer, start, sweep));
            }
        }
    }

    internal double OuterRadius => Sectors.Count == 0 ? CenterRadius + 40 : Sectors.Max(s => s.Outer);

    internal IEnumerable<(WheelMode Mode, Rect Bounds)> ModeChips()
    {
        const double width = 92, height = 28, gap = 8;
        var total = Modes.Length * width + (Modes.Length - 1) * gap;
        var top = WheelSize / 2 + OuterRadius + RindWidth + 14;
        for (var i = 0; i < Modes.Length; i++)
            yield return (Modes[i], new Rect(WheelSize / 2 - total / 2 + i * (width + gap), top, width, height));
    }

    /// <summary>The current pointer position in canvas coordinates (DIPs, wheel center at WheelSize/2).</summary>
    private Point CursorOnCanvas()
    {
        var cursor = Native.CursorPixels();
        return new Point(WheelSize / 2 + (cursor.X - _placedAt.X) / _scale, WheelSize / 2 + (cursor.Y - _placedAt.Y) / _scale);
    }

    // Not Shift+Alt or Ctrl+Shift: those switch the Windows input language. Ctrl+Alt is AltGr, so it means nothing.
    private static WheelMode? ModeFor(DragModifiers m) =>
        m.HasFlag(DragModifiers.Alt) && !m.HasFlag(DragModifiers.Control) ? WheelMode.Tools
        : m.HasFlag(DragModifiers.Control) && !m.HasFlag(DragModifiers.Alt) ? WheelMode.Actions
        : m.HasFlag(DragModifiers.Shift) ? WheelMode.Convert
        : null;

    private static DragModifiers ModifiersFrom(DragDropKeyStates keys)
    {
        var m = DragModifiers.None;
        if (keys.HasFlag(DragDropKeyStates.ShiftKey)) m |= DragModifiers.Shift;
        if (keys.HasFlag(DragDropKeyStates.ControlKey)) m |= DragModifiers.Control;
        if (keys.HasFlag(DragDropKeyStates.AltKey)) m |= DragModifiers.Alt;
        return m;
    }

    // ---------- Hit testing ----------

    private void UpdateHover(Point p)
    {
        var hover = -1;
        var hoverMode = -1;
        var dx = p.X - WheelSize / 2;
        var dy = p.Y - WheelSize / 2;
        var r = Math.Sqrt(dx * dx + dy * dy);
        var angle = Math.Atan2(dy, dx) * 180 / Math.PI;
        for (var i = 0; i < Sectors.Count; i++)
        {
            var s = Sectors[i];
            if (r < s.Inner || r > s.Outer) continue;
            var rel = ((angle - s.Start) % 360 + 360) % 360;
            if (rel <= s.Sweep)
            {
                hover = i;
                break;
            }
        }
        var chips = ModeChips().ToList();
        for (var i = 0; i < chips.Count; i++)
            if (chips[i].Bounds.Contains(p)) hoverMode = i;

        if (hover != Hover || hoverMode != HoverMode)
        {
            Hover = hover;
            HoverMode = hoverMode;
            _canvas.InvalidateVisual();
        }
    }

    // ---------- Drag & drop ----------

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        e.Handled = true;
        if (_clickMode) return;
        if (!Revealed)
        {
            Diag.Log($"wheel: drag enter formats=[{string.Join(", ", e.Data.GetFormats())}] keys={e.KeyStates}");
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files)
            {
                e.Effects = DragDropEffects.None;
                return;
            }
            _files = files;
            Mode = ModeFor(ModifiersFrom(e.KeyStates)) ?? Mode;
            Reveal();
        }
        UpdateHover(e.GetPosition(_canvas));
        e.Effects = Hover >= 0 ? DragDropEffects.Copy : DragDropEffects.None;
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        e.Handled = true;
        if (!Revealed)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        if (ModeFor(ModifiersFrom(e.KeyStates)) is { } mode) SetMode(mode);
        UpdateHover(e.GetPosition(_canvas));
        if (HoverMode >= 0) SetMode(Modes[HoverMode]);
        e.Effects = Hover >= 0 ? DragDropEffects.Copy : DragDropEffects.None;
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        base.OnDragLeave(e);
        e.Handled = true;
        if (_clickMode || !Revealed) return;
        // DragLeave carries no usable position (always 0,0), so ask the system where the pointer really is.
        // Leaves can also be spurious (e.g. while the window redraws); only dismiss when truly outside.
        var p = CursorOnCanvas();
        var r = Math.Sqrt(Math.Pow(p.X - WheelSize / 2, 2) + Math.Pow(p.Y - WheelSize / 2, 2));
        var onChips = ModeChips().Any(c => c.Bounds.Contains(p));
        Diag.Log($"wheel: drag leave r={r:0} outer={OuterRadius:0} onChips={onChips}");
        if (r > OuterRadius + RindWidth && !onChips) Dismiss();
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        e.Handled = true;
        if (_dropped || !Revealed) return;
        _dropped = true;
        UpdateHover(e.GetPosition(_canvas));
        var item = Hover >= 0 ? Sectors[Hover].Item : null;
        Diag.Log($"wheel: drop on {(item?.Label ?? "nothing")}");
        e.Effects = item is null ? DragDropEffects.None : DragDropEffects.Copy;
        Dismiss();
        // Let Explorer's drag loop finish before showing dialogs or windows.
        if (item is not null) Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Run(item));
    }

    // ---------- Click mode ----------

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_clickMode) UpdateHover(e.GetPosition(_canvas));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_clickMode) return;
        UpdateHover(e.GetPosition(_canvas));
        if (HoverMode >= 0)
        {
            SetMode(Modes[HoverMode]);
            return;
        }
        var item = Hover >= 0 ? Sectors[Hover].Item : null;
        Dismiss();
        if (item is not null) Dispatcher.BeginInvoke(() => Run(item));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Escape: Dismiss(); break;
            case Key.Tab: SetMode(Modes[(Array.IndexOf(Modes, Mode) + 1) % Modes.Length]); break;
        }
    }

    private static void Run(WheelItem item)
    {
        try
        {
            item.Run();
        }
        catch (Exception ex)
        {
            Dialogs.Error(item.Label, ex.Message);
        }
    }

    internal sealed record Sector(WheelItem Item, double Inner, double Outer, double Start, double Sweep);

    // ---------- Rendering ----------

    private sealed class WheelCanvas(WheelWindow owner) : FrameworkElement
    {
        // Lemon cross-section palette: rind, white pith/membranes, pale pulp, dark ink for text.
        private static readonly Brush HitBrush = Freeze(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)));
        private static readonly Brush Pith = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xFC, 0xEE)));
        private static readonly Brush ChipHover = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xF0, 0xB0)));
        private static readonly Brush Peel = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xD2, 0x3F)));
        private static readonly Pen RindEdge = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xE3, 0xA4, 0x00)), 1.5));
        private static readonly Pen PithEdge = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xEE, 0xDC, 0x9A)), 1));
        private static readonly Brush Ink = Freeze(new SolidColorBrush(Color.FromRgb(0x3B, 0x30, 0x00)));
        private static readonly Brush InkDim = Freeze(new SolidColorBrush(Color.FromRgb(0x7A, 0x68, 0x22)));
        private static readonly Typeface UiFace = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        private static readonly Typeface UiFaceRegular = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        private static readonly Typeface IconFace = new(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        private const double CornerRadius = 9;
        private const double MembraneWidth = 5;
        private readonly Dictionary<Sector, Geometry> _shapes = [];

        private static T Freeze<T>(T f) where T : Freezable
        {
            f.Freeze();
            return f;
        }

        private static Brush Radial(Point c, double radius, params (double Offset, Color Color)[] stops)
        {
            var brush = new RadialGradientBrush { MappingMode = BrushMappingMode.Absolute, Center = c, GradientOrigin = c, RadiusX = radius, RadiusY = radius };
            foreach (var (offset, color) in stops) brush.GradientStops.Add(new GradientStop(color, offset));
            brush.Freeze();
            return brush;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var c = new Point(WheelSize / 2, WheelSize / 2);
            if (!owner.Revealed)
            {
                // Nearly invisible target so the OLE drag loop finds this window under the cursor.
                dc.DrawEllipse(HitBrush, null, c, 70, 70);
                return;
            }

            var outer = owner.OuterRadius;
            var pulp = Radial(c, outer,
                (0.0, Color.FromRgb(0xFF, 0xFB, 0xDD)), (0.55, Color.FromRgb(0xFF, 0xEE, 0x96)), (1.0, Color.FromRgb(0xFF, 0xDE, 0x55)));
            var pulpHover = Radial(c, outer,
                (0.0, Color.FromRgb(0xFF, 0xE0, 0x5C)), (1.0, Color.FromRgb(0xFF, 0xB8, 0x00)));
            var rind = Radial(c, outer + RindWidth,
                (0.0, Color.FromRgb(0xFF, 0xE2, 0x6A)), ((outer + PithWidth) / (outer + RindWidth), Color.FromRgb(0xFF, 0xD8, 0x45)), (1.0, Color.FromRgb(0xF2, 0xB0, 0x00)));

            // Rind, then the white pith that also shows through as membranes between segments.
            dc.DrawEllipse(rind, RindEdge, c, outer + RindWidth, outer + RindWidth);
            dc.DrawEllipse(Pith, null, c, outer + PithWidth, outer + PithWidth);

            for (var i = 0; i < owner.Sectors.Count; i++)
                DrawSector(dc, c, owner.Sectors[i], i == owner.Hover, i == owner.Hover ? pulpHover : pulp);

            // Center: the lemon's core, with the file preview, count and mode.
            var hoverCancel = owner.Hover < 0;
            dc.DrawEllipse(Pith, PithEdge, c, CenterRadius, CenterRadius);
            if (owner.Thumbnail is { } thumb)
            {
                const double t = 44;
                var ratio = thumb.PixelWidth / (double)Math.Max(1, thumb.PixelHeight);
                var w = ratio >= 1 ? t : t * ratio;
                var h = ratio >= 1 ? t / ratio : t;
                dc.DrawImage(thumb, new Rect(c.X - w / 2, c.Y - 36, w, h));
            }
            else if (!owner.ThumbnailPending)
            {
                // No preview available: a generic document glyph.
                DrawText(dc, "", 30, IconFace, Ink, new Point(c.X, c.Y - 14));
            }
            var count = owner.Files.Count;
            var caption = owner.Sectors.Count == 0 ? Loc.T("Nothing here") :
                count == 1 ? Ellipsize(Path.GetFileName(owner.Files[0]), 16) : Loc.F("{0} items", count);
            DrawText(dc, caption, 11, UiFace, Ink, new Point(c.X, c.Y + 18));
            DrawText(dc, hoverCancel && !owner.ClickMode ? Loc.T("release to cancel") : Loc.T(owner.Mode.ToString()), 10, UiFaceRegular, InkDim, new Point(c.X, c.Y + 34));

            // Mode chips.
            var chips = owner.ModeChips().ToList();
            for (var i = 0; i < chips.Count; i++)
            {
                var (mode, bounds) = chips[i];
                var active = mode == owner.Mode;
                var fill = active ? Peel : i == owner.HoverMode ? ChipHover : Pith;
                dc.DrawRoundedRectangle(fill, active ? RindEdge : PithEdge, bounds, 14, 14);
                var label = mode switch
                {
                    WheelMode.Convert => Loc.T("Convert"),
                    WheelMode.Tools => Loc.T("Tools"),
                    _ => Loc.T("Actions"),
                };
                DrawText(dc, label, 12, UiFace, Ink, new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
            }
            if (!owner.ClickMode)
            {
                var hintY = chips[0].Bounds.Bottom + 18;
                dc.DrawRoundedRectangle(Pith, PithEdge, new Rect(c.X - 180, hintY - 10, 360, 20), 10, 10);
                DrawText(dc, Loc.T("Shift: Convert   ·   Alt: Tools   ·   Ctrl: Actions"), 10.5, UiFaceRegular, InkDim, new Point(c.X, hintY));
            }
        }

        private void DrawSector(DrawingContext dc, Point c, Sector s, bool hover, Brush fill)
        {
            if (!_shapes.TryGetValue(s, out var geometry))
                _shapes[s] = geometry = SegmentGeometry(c, s.Inner, s.Outer, s.Start, s.Sweep);
            dc.DrawGeometry(fill, null, geometry);

            var mid = (s.Start + s.Sweep / 2) * Math.PI / 180;
            var radius = (s.Inner + s.Outer) / 2;
            var p = new Point(c.X + radius * Math.Cos(mid), c.Y + radius * Math.Sin(mid));
            var dim = hover ? Ink : InkDim;
            // Room for a line of text: the sector chord at mid radius, capped by the ring thickness.
            var chord = s.Sweep >= 180 ? double.MaxValue : 2 * radius * Math.Sin(s.Sweep / 2 * Math.PI / 180);
            var maxWidth = Math.Min(chord * 0.78, (s.Outer - s.Inner) * 1.25);

            var y = p.Y;
            if (s.Item.Glyph is not null) y -= 10;
            if (s.Item.Caption is not null) y -= 6;
            if (s.Item.Glyph is { } glyph)
            {
                if (glyph == Glyphs.IdCard) DrawIdCard(dc, new Point(p.X, y));
                else DrawText(dc, glyph, 18, IconFace, Ink, new Point(p.X, y));
                y += 22;
            }
            DrawText(dc, Loc.T(s.Item.Label), s.Item.Glyph is null ? 15 : 12, UiFace, Ink, new Point(p.X, y), maxWidth);
            if (s.Item.Caption is { } caption)
                DrawText(dc, Loc.T(caption), 9.5, UiFaceRegular, dim, new Point(p.X, y + 16), maxWidth);
        }

        /// <summary>A small ID card the size of an icon glyph: rounded outline, a photo on the left, text lines on the right.</summary>
        private static void DrawIdCard(DrawingContext dc, Point center)
        {
            var pen = new Pen(Ink, 1.3);
            dc.DrawRoundedRectangle(null, pen, new Rect(center.X - 11, center.Y - 7.5, 22, 15), 2.5, 2.5);
            dc.DrawEllipse(Ink, null, new Point(center.X - 5.5, center.Y - 2), 2, 2);
            dc.DrawRoundedRectangle(Ink, null, new Rect(center.X - 8.5, center.Y + 0.8, 6, 3.2), 1.6, 1.6);
            var thin = new Pen(Ink, 1.1);
            dc.DrawLine(thin, new Point(center.X + 0.5, center.Y - 3), new Point(center.X + 7.5, center.Y - 3));
            dc.DrawLine(thin, new Point(center.X + 0.5, center.Y), new Point(center.X + 7.5, center.Y));
            dc.DrawLine(thin, new Point(center.X + 0.5, center.Y + 3), new Point(center.X + 5, center.Y + 3));
        }

        /// <summary>Centered text; when <paramref name="maxWidth"/> is given it shrinks (down to 75%) and then trims to fit.</summary>
        private void DrawText(DrawingContext dc, string text, double size, Typeface face, Brush brush, Point center, double maxWidth = double.MaxValue)
        {
            var ft = Format(text, size, face, brush);
            if (ft.Width > maxWidth)
            {
                ft = Format(text, Math.Max(size * 0.75, size * maxWidth / ft.Width), face, brush);
                if (ft.Width > maxWidth)
                {
                    ft.MaxTextWidth = maxWidth;
                    ft.MaxLineCount = 1;
                    ft.Trimming = TextTrimming.CharacterEllipsis;
                }
            }
            dc.DrawText(ft, new Point(center.X - Math.Min(ft.Width, maxWidth) / 2, center.Y - ft.Height / 2));
        }

        private FormattedText Format(string text, double size, Typeface face, Brush brush) =>
            new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        private static string Ellipsize(string text, int max) => text.Length <= max ? text : text[..Math.Max(1, max - 1)] + "…";

        /// <summary>
        /// A lemon segment: the annular sector minus a constant-width membrane, with rounded corners.
        /// Built by shrinking the sector by the corner radius, then widening it back with round joins.
        /// </summary>
        private static Geometry SegmentGeometry(Point c, double inner, double outer, double startDeg, double sweepDeg)
        {
            const double r = CornerRadius;
            var ri = inner + MembraneWidth / 2 + r;
            var ro = outer - MembraneWidth / 2 - r;
            Geometry core;
            if (sweepDeg >= 359.9)
            {
                // A single item fills the whole ring: no side membranes.
                core = new CombinedGeometry(GeometryCombineMode.Exclude, new EllipseGeometry(c, ro, ro), new EllipseGeometry(c, ri, ri));
            }
            else
            {
                // Side edges run parallel to the radial dividers, so membranes keep the same width everywhere.
                var d = MembraneWidth / 2 + r;
                var a0 = startDeg * Math.PI / 180;
                var a1 = (startDeg + sweepDeg) * Math.PI / 180;
                Point Corner(double radius, double angle, double side)
                {
                    var rho = Math.Sqrt(Math.Max(0, radius * radius - d * d));
                    var (cos, sin) = (Math.Cos(angle), Math.Sin(angle));
                    // Offset toward the inside of the sector: +normal on the start edge, -normal on the end edge.
                    return new Point(c.X + rho * cos - side * d * sin, c.Y + rho * sin + side * d * cos);
                }
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    var large = sweepDeg > 180;
                    ctx.BeginFigure(Corner(ri, a0, 1), true, true);
                    ctx.LineTo(Corner(ro, a0, 1), true, true);
                    ctx.ArcTo(Corner(ro, a1, -1), new Size(ro, ro), 0, large, SweepDirection.Clockwise, true, true);
                    ctx.LineTo(Corner(ri, a1, -1), true, true);
                    ctx.ArcTo(Corner(ri, a0, 1), new Size(ri, ri), 0, large, SweepDirection.Counterclockwise, true, true);
                }
                core = g;
            }
            var rounded = Geometry.Combine(core,
                core.GetWidenedPathGeometry(new Pen(Brushes.Black, 2 * r) { LineJoin = PenLineJoin.Round }),
                GeometryCombineMode.Union, null);
            rounded.Freeze();
            return rounded;
        }
    }
}
