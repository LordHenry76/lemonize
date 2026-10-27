using System.IO;
using System.Windows;
using ImageMagick;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Lemonize.Engines;

public enum ScanLook { BlackAndWhite, Color, Original }

/// <summary>
/// One photo to scan. Corners are top-left, top-right, bottom-right, bottom-left in the photo's pixels after
/// rotation; null means detect them (or take the whole photo).
/// </summary>
public sealed record ScanPage(string File, int Rotation, Point[]? Corners, ScanLook Look, double Whitening);

/// <summary>
/// Turns photos of paper documents into flat A4 pages: finds the sheet, undoes the perspective,
/// evens out shadows and lighting.
/// </summary>
public static class ScanEngine
{
    private const double Dpi = 300;
    private static readonly int A4Short = (int)Math.Round(210 / 25.4 * Dpi);
    private static readonly int A4Long = (int)Math.Round(297 / 25.4 * Dpi);

    /// <summary>
    /// Finds the sheet as a bright, colorless region that fills the quadrilateral of its own corners and returns
    /// those corners; null when nothing sheet-like covers a fair part of the photo.
    /// </summary>
    public static Point[]? DetectSheet(MagickImage image)
    {
        using var small = (MagickImage)image.Clone();
        small.Resize(new MagickGeometry(500, 500));
        small.Blur(0, 1.5);
        int w = (int)small.Width, h = (int)small.Height;
        using var pixels = small.GetPixels();
        var rgb = pixels.ToByteArray(PixelMapping.RGB)!;

        // Paper is bright in every channel; cream tablecloths and wood are not bright in blue.
        var whiteness = new byte[w * h];
        var histogram = new int[256];
        for (var i = 0; i < whiteness.Length; i++)
        {
            whiteness[i] = Math.Min(rgb[i * 3], Math.Min(rgb[i * 3 + 1], rgb[i * 3 + 2]));
            histogram[whiteness[i]]++;
        }
        // A light background can merge with the sheet at Otsu's threshold, so try stricter ones too. A sheet fills
        // the quadrilateral of its own corners, a blob of wall doesn't; among the ones that do, the biggest wins,
        // since stricter thresholds start eating into the shaded parts of the paper.
        var otsu = Otsu(histogram, whiteness.Length);
        Point[]? best = null;
        var bestArea = 0;
        for (var threshold = otsu; threshold < 250; threshold += 6)
        {
            var mask = new bool[w * h];
            for (var i = 0; i < mask.Length; i++) mask[i] = whiteness[i] > threshold;

            // Opening cuts thin bridges (patterns, plastic sleeves) that would glue the sheet to the background.
            const int open = 3;
            for (var k = 0; k < open; k++) mask = Morph(mask, w, h, erode: true);
            for (var k = 0; k < open; k++) mask = Morph(mask, w, h, erode: false);

            var regions = Regions(mask, w, h).Where(r => r.Count > w * h * 0.12).ToList();
            if (regions.Count == 0) break;
            foreach (var region in regions)
            {
                // On a convex shape each diagonal direction peaks at a corner.
                int tl = region[0], tr = tl, br = tl, bl = tl;
                foreach (var p in region)
                {
                    int x = p % w, y = p / w;
                    if (x + y < tl % w + tl / w) tl = p;
                    if (x + y > br % w + br / w) br = p;
                    if (x - y > tr % w - tr / w) tr = p;
                    if (x - y < bl % w - bl / w) bl = p;
                }
                Point[] corners = [.. new[] { tl, tr, br, bl }.Select(p => new Point(p % w + 0.5, p / w + 0.5))];
                var solid = SolidArea(region, w, h);
                var fill = solid / Math.Max(1, Area(corners));
                if (fill > 0.9 && solid > bestArea && Plausible(corners))
                {
                    bestArea = solid;
                    best = corners;
                }
            }
        }
        if (best is null) return null;
        var scale = image.Width / (double)w;
        return [.. best.Select(p => new Point(p.X * scale, p.Y * scale))];
    }

    /// <summary>A photographed sheet: no corner too sharp or too flat, opposite sides of similar length.</summary>
    private static bool Plausible(Point[] corners)
    {
        for (var i = 0; i < 4; i++)
        {
            var angle = Vector.AngleBetween(corners[(i + 3) % 4] - corners[i], corners[(i + 1) % 4] - corners[i]);
            if (Math.Abs(angle) is < 55 or > 125) return false;
        }
        double top = Distance(corners[0], corners[1]), bottom = Distance(corners[3], corners[2]);
        double left = Distance(corners[0], corners[3]), right = Distance(corners[1], corners[2]);
        return Math.Min(top, bottom) / Math.Max(top, bottom) > 0.6 && Math.Min(left, right) / Math.Max(left, right) > 0.6;
    }

    /// <summary>Pixels of the region plus the holes it encloses (text, stamps), i.e. its silhouette.</summary>
    private static int SolidArea(List<int> region, int w, int h)
    {
        var inside = new bool[w * h];
        foreach (var p in region) inside[p] = true;
        var outside = new bool[w * h];
        var stack = new Stack<int>();
        for (var x = 0; x < w; x++)
        {
            Seed(x);
            Seed((h - 1) * w + x);
        }
        for (var y = 0; y < h; y++)
        {
            Seed(y * w);
            Seed(y * w + w - 1);
        }
        var reached = 0;
        while (stack.Count > 0)
        {
            var p = stack.Pop();
            reached++;
            int x = p % w, y = p / w;
            if (x > 0) Seed(p - 1);
            if (x < w - 1) Seed(p + 1);
            if (y > 0) Seed(p - w);
            if (y < h - 1) Seed(p + w);
        }
        return w * h - reached;

        void Seed(int q)
        {
            if (inside[q] || outside[q]) return;
            outside[q] = true;
            stack.Push(q);
        }
    }

    private static double Area(IReadOnlyList<Point> polygon)
    {
        var sum = 0.0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var (a, b) = (polygon[i], polygon[(i + 1) % polygon.Count]);
            sum += a.X * b.Y - b.X * a.Y;
        }
        return Math.Abs(sum) / 2;
    }

    /// <summary>The whole photo minus a small border, for when no sheet is found.</summary>
    public static Point[] DefaultCorners(double width, double height)
    {
        double mx = width * 0.05, my = height * 0.05;
        return [new(mx, my), new(width - mx, my), new(width - mx, height - my), new(mx, height - my)];
    }

    /// <summary>Maps the quadrilateral onto an A4 page, portrait or landscape following the sheet's shape.</summary>
    public static MagickImage Straighten(MagickImage photo, IReadOnlyList<Point> corners, int longSide = 0)
    {
        if (longSide <= 0) longSide = A4Long;
        var shortSide = (int)Math.Round(longSide * A4Short / (double)A4Long);
        var across = (Distance(corners[0], corners[1]) + Distance(corners[3], corners[2])) / 2;
        var down = (Distance(corners[0], corners[3]) + Distance(corners[1], corners[2])) / 2;
        var (w, h) = across > down ? (longSide, shortSide) : (shortSide, longSide);

        Point[] target = [new(0, 0), new(w, 0), new(w, h), new(0, h)];
        var args = new double[16];
        for (var i = 0; i < 4; i++)
        {
            args[i * 4] = corners[i].X;
            args[i * 4 + 1] = corners[i].Y;
            args[i * 4 + 2] = target[i].X;
            args[i * 4 + 3] = target[i].Y;
        }
        var page = (MagickImage)photo.Clone();
        page.Alpha(AlphaAction.Off);
        page.BackgroundColor = MagickColors.White;
        page.VirtualPixelMethod = VirtualPixelMethod.Background;
        page.Distort(new DistortSettings(DistortMethod.Perspective) { Viewport = new MagickGeometry(0, 0, (uint)w, (uint)h) }, args);
        page.ResetPage();
        return page;
    }

    /// <summary>
    /// Divides out the paper's own shading (estimated with a filter wider than any stroke), then blends with
    /// the original by <paramref name="whitening"/> (0–1). Black and white comes out as a grayscale page.
    /// </summary>
    public static void Clean(MagickImage page, ScanLook look, double whitening)
    {
        if (look == ScanLook.Original) return;
        int w = (int)page.Width, h = (int)page.Height;

        using var gray = (MagickImage)page.Clone();
        gray.Grayscale();
        byte[] luma;
        using (var pixels = gray.GetPixels()) luma = pixels.ToByteArray("R")!;

        // Max then median on a quarter-size copy removes text and keeps the slow light falloff across the sheet.
        // Beyond the page the filters must see more paper: the white fill Straighten leaves set would make the
        // paper look brighter near the edges and darken a frame all around the page.
        gray.VirtualPixelMethod = VirtualPixelMethod.Edge;
        gray.Resize(new Percentage(25));
        gray.Statistic(StatisticType.Maximum, 3, 3);
        gray.Statistic(StatisticType.Median, 11, 11);
        gray.Blur(0, 3);
        gray.Resize(new MagickGeometry((uint)w, (uint)h) { IgnoreAspectRatio = true });
        byte[] paper;
        using (var pixels = gray.GetPixels()) paper = pixels.ToByteArray("R")!;

        if (look == ScanLook.BlackAndWhite)
        {
            var result = new byte[w * h];
            for (var i = 0; i < result.Length; i++)
            {
                var flat = Math.Min(255.0, luma[i] * 255.0 / Math.Max((byte)1, paper[i]));
                result[i] = (byte)Math.Round((1 - whitening) * luma[i] + whitening * flat);
            }
            page.ReadPixels(result, new PixelReadSettings((uint)w, (uint)h, StorageType.Char, "I"));
            return;
        }

        byte[] rgb;
        using (var pixels = page.GetPixels()) rgb = pixels.ToByteArray(PixelMapping.RGB)!;
        for (var i = 0; i < w * h; i++)
        {
            var gain = 1 + whitening * (Math.Max(1.0, 255.0 / Math.Max((byte)1, paper[i])) - 1);
            for (var c = 0; c < 3; c++)
                rgb[i * 3 + c] = (byte)Math.Min(255.0, Math.Round(rgb[i * 3 + c] * gain));
        }
        page.ReadPixels(rgb, new PixelReadSettings((uint)w, (uint)h, StorageType.Char, PixelMapping.RGB));
    }

    /// <summary>Loads a page's photo in the orientation the user sees it.</summary>
    public static MagickImage LoadPhoto(string file, int rotation)
    {
        var photo = ImageEngine.Load(file);
        if (rotation % 360 != 0) photo.Rotate(rotation);
        photo.ResetPage();
        return photo;
    }

    /// <summary>Writes every page as a full-bleed A4 page at 300 dpi.</summary>
    public static void ToPdf(IReadOnlyList<ScanPage> pages, string output, Action<double>? progress = null)
    {
        using var document = new PdfDocument();
        for (var i = 0; i < pages.Count; i++)
        {
            var scan = pages[i];
            using var photo = LoadPhoto(scan.File, scan.Rotation);
            var corners = scan.Corners ?? DetectSheet(photo) ?? DefaultCorners(photo.Width, photo.Height);
            using var image = Straighten(photo, corners);
            Clean(image, scan.Look, scan.Whitening);
            image.Format = MagickFormat.Jpeg;
            image.Quality = 85;
            if (scan.Look == ScanLook.BlackAndWhite) image.ColorType = ColorType.Grayscale;
            using var stream = new MemoryStream(image.ToByteArray());
            using var ximage = XImage.FromStream(stream);

            var landscape = image.Width > image.Height;
            var page = document.AddPage();
            page.Width = XUnit.FromMillimeter(landscape ? 297 : 210);
            page.Height = XUnit.FromMillimeter(landscape ? 210 : 297);
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawImage(ximage, 0, 0, page.Width.Point, page.Height.Point);
            progress?.Invoke((i + 1.0) / pages.Count);
        }
        document.Save(output);
    }

    private static double Distance(Point a, Point b) => (a - b).Length;

    private static int Otsu(int[] histogram, int total)
    {
        double sum = 0;
        for (var i = 0; i < 256; i++) sum += i * (double)histogram[i];
        double sumBelow = 0, best = -1;
        int below = 0, threshold = 127;
        for (var t = 0; t < 256; t++)
        {
            below += histogram[t];
            if (below == 0) continue;
            var above = total - below;
            if (above == 0) break;
            sumBelow += t * (double)histogram[t];
            var meanBelow = sumBelow / below;
            var meanAbove = (sum - sumBelow) / above;
            var between = (double)below * above * (meanBelow - meanAbove) * (meanBelow - meanAbove);
            if (between > best)
            {
                best = between;
                threshold = t;
            }
        }
        return threshold;
    }

    private static bool[] Morph(bool[] mask, int w, int h, bool erode)
    {
        var result = new bool[mask.Length];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var value = erode;
                for (var dy = -1; dy <= 1 && value == erode; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        // Outside the photo counts as background, so a sheet touching the edge still erodes there.
                        var inside = nx >= 0 && ny >= 0 && nx < w && ny < h && mask[ny * w + nx];
                        if (inside != erode)
                        {
                            value = !erode;
                            break;
                        }
                    }
                result[y * w + x] = value;
            }
        return result;
    }

    private static List<List<int>> Regions(bool[] mask, int w, int h)
    {
        var seen = new bool[mask.Length];
        var regions = new List<List<int>>();
        var stack = new Stack<int>();
        for (var start = 0; start < mask.Length; start++)
        {
            if (!mask[start] || seen[start]) continue;
            var region = new List<int>();
            seen[start] = true;
            stack.Push(start);
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                region.Add(p);
                int x = p % w, y = p / w;
                if (x > 0) Visit(p - 1);
                if (x < w - 1) Visit(p + 1);
                if (y > 0) Visit(p - w);
                if (y < h - 1) Visit(p + w);
            }
            regions.Add(region);
        }
        return regions;

        void Visit(int q)
        {
            if (!mask[q] || seen[q]) return;
            seen[q] = true;
            stack.Push(q);
        }
    }
}
