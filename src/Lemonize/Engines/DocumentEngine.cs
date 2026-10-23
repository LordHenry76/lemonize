using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Lemonize.Core;
using PdfSharp.Drawing;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Lemonize.Engines;

public enum BlockKind { Heading, Paragraph, PageBreak }

public sealed record DocBlock(BlockKind Kind, string Text = "");

public sealed record DocContent(string Title, List<DocBlock> Blocks);

/// <summary>Document conversions that need no external software, plus Office/LibreOffice bridges.</summary>
public static partial class DocumentEngine
{
    // ---------- Readers ----------

    public static DocContent Read(string path)
    {
        var title = OutputPaths.BaseName(path);
        return FileKinds.Ext(path) switch
        {
            "docx" => ReadDocx(path),
            "epub" => ReadEpub(path),
            "pdf" => new DocContent(title, PdfEngine.ExtractPageTexts(path)
                .SelectMany((page, i) => Paragraphs(page).Prepend(i > 0 ? new DocBlock(BlockKind.PageBreak) : null))
                .OfType<DocBlock>().ToList()),
            "md" or "markdown" => ReadMarkdown(path),
            _ => new DocContent(title, Paragraphs(ReadAllTextSmart(path)).ToList()),
        };
    }

    private static IEnumerable<DocBlock> Paragraphs(string text) =>
        Regex.Split(text.Replace("\r\n", "\n"), @"\n\s*\n")
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Select(p => new DocBlock(BlockKind.Paragraph, Regex.Replace(p, @"\s*\n\s*", " ")));

    private static DocContent ReadMarkdown(string path)
    {
        var blocks = new List<DocBlock>();
        var paragraph = new StringBuilder();
        void Flush()
        {
            if (paragraph.Length > 0) blocks.Add(new DocBlock(BlockKind.Paragraph, paragraph.ToString().Trim()));
            paragraph.Clear();
        }
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith('#'))
            {
                Flush();
                blocks.Add(new DocBlock(BlockKind.Heading, line.TrimStart('#').Trim()));
            }
            else if (line.Length == 0) Flush();
            else paragraph.Append(Regex.Replace(line, @"[*_`]{1,3}", "")).Append(' ');
        }
        Flush();
        return new DocContent(OutputPaths.BaseName(path), blocks);
    }

    public static string ReadAllTextSmart(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private static DocContent ReadDocx(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document.Body ?? throw new InvalidDataException(Loc.T("The document has no body."));
        var blocks = new List<DocBlock>();
        foreach (var element in body.ChildElements)
        {
            switch (element)
            {
                case W.Paragraph p:
                    if (p.Descendants<W.Break>().Any(b => b.Type?.Value == W.BreakValues.Page))
                        blocks.Add(new DocBlock(BlockKind.PageBreak));
                    var text = p.InnerText.Trim();
                    if (text.Length == 0) continue;
                    var style = p.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "";
                    var isHeading = style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) ||
                                    style.StartsWith("Titolo", StringComparison.OrdinalIgnoreCase) ||
                                    style.Equals("Title", StringComparison.OrdinalIgnoreCase) ||
                                    p.ParagraphProperties?.OutlineLevel is not null;
                    blocks.Add(new DocBlock(isHeading ? BlockKind.Heading : BlockKind.Paragraph, text));
                    break;
                case W.Table table:
                    foreach (var row in table.Elements<W.TableRow>())
                        blocks.Add(new DocBlock(BlockKind.Paragraph,
                            string.Join("\t", row.Elements<W.TableCell>().Select(c => c.InnerText.Trim()))));
                    break;
            }
        }
        var title = doc.PackageProperties.Title;
        return new DocContent(string.IsNullOrWhiteSpace(title) ? OutputPaths.BaseName(path) : title, blocks);
    }

    private static DocContent ReadEpub(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var container = XDocument.Load(zip.GetEntry("META-INF/container.xml")!.Open());
        var opfPath = container.Descendants().First(e => e.Name.LocalName == "rootfile").Attribute("full-path")!.Value;
        var opf = XDocument.Load(zip.GetEntry(opfPath)!.Open());
        var baseDir = Path.GetDirectoryName(opfPath)?.Replace('\\', '/') ?? "";

        var manifest = opf.Descendants().Where(e => e.Name.LocalName == "item")
            .ToDictionary(e => e.Attribute("id")!.Value, e => e.Attribute("href")!.Value);
        var title = opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "title")?.Value ?? OutputPaths.BaseName(path);

        var blocks = new List<DocBlock>();
        foreach (var itemref in opf.Descendants().Where(e => e.Name.LocalName == "itemref"))
        {
            if (!manifest.TryGetValue(itemref.Attribute("idref")!.Value, out var href)) continue;
            var entryPath = (baseDir.Length > 0 ? baseDir + "/" : "") + Uri.UnescapeDataString(href.Split('#')[0]);
            var entry = zip.GetEntry(entryPath);
            if (entry is null) continue;
            using var reader = new StreamReader(entry.Open());
            var html = reader.ReadToEnd();
            if (blocks.Count > 0) blocks.Add(new DocBlock(BlockKind.PageBreak));
            foreach (Match m in HtmlBlockRegex().Matches(html))
            {
                var text = WebUtility.HtmlDecode(TagRegex().Replace(m.Groups[2].Value, "")).Trim();
                text = Regex.Replace(text, @"\s+", " ");
                if (text.Length == 0) continue;
                blocks.Add(new DocBlock(m.Groups[1].Value.StartsWith('h') ? BlockKind.Heading : BlockKind.Paragraph, text));
            }
        }
        return new DocContent(title, blocks);
    }

    [GeneratedRegex(@"<(h[1-6]|p|li)\b[^>]*>(.*?)</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex HtmlBlockRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    // ---------- Writers ----------

    public static void Write(DocContent content, string output)
    {
        switch (FileKinds.Ext(output))
        {
            case "txt":
                File.WriteAllText(output, ToPlainText(content), new UTF8Encoding(false));
                break;
            case "docx":
                WriteDocx(content, output);
                break;
            case "pdf":
                WritePdf(content, output);
                break;
            case "epub":
                WriteEpub(content, output);
                break;
            default:
                throw new NotSupportedException(Loc.T("Unsupported document target."));
        }
    }

    private static string ToPlainText(DocContent content)
    {
        var sb = new StringBuilder();
        foreach (var block in content.Blocks)
        {
            if (block.Kind == BlockKind.PageBreak) sb.AppendLine();
            else sb.AppendLine(block.Text).AppendLine();
        }
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static void WriteDocx(DocContent content, string output)
    {
        using var doc = WordprocessingDocument.Create(output, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        var body = new W.Body();
        foreach (var block in content.Blocks)
        {
            if (block.Kind == BlockKind.PageBreak)
            {
                body.Append(new W.Paragraph(new W.Run(new W.Break { Type = W.BreakValues.Page })));
                continue;
            }
            var runProps = block.Kind == BlockKind.Heading
                ? new W.RunProperties(new W.Bold(), new W.FontSize { Val = "32" })
                : new W.RunProperties();
            var paraProps = new W.ParagraphProperties(new W.SpacingBetweenLines { After = "160" });
            if (block.Kind == BlockKind.Heading) paraProps.Append(new W.OutlineLevel { Val = 0 });
            body.Append(new W.Paragraph(paraProps, new W.Run(runProps, new W.Text(block.Text) { Space = SpaceProcessingModeValues.Preserve })));
        }
        main.Document = new W.Document(body);
        doc.PackageProperties.Title = content.Title;
    }

    private static void WritePdf(DocContent content, string output)
    {
        PdfEngine.EnsureFonts();
        using var document = new PdfSharp.Pdf.PdfDocument();
        document.Info.Title = content.Title;
        var bodyFont = new XFont("Segoe UI", 11);
        var headingFont = new XFont("Segoe UI", 17, XFontStyleEx.Bold);
        const double margin = 60;

        PdfSharp.Pdf.PdfPage? page = null;
        XGraphics? gfx = null;
        double y = 0;
        void NewPage()
        {
            gfx?.Dispose();
            page = document.AddPage();
            page.Width = XUnit.FromMillimeter(210);
            page.Height = XUnit.FromMillimeter(297);
            gfx = XGraphics.FromPdfPage(page);
            y = margin;
        }
        NewPage();

        foreach (var block in content.Blocks)
        {
            if (block.Kind == BlockKind.PageBreak)
            {
                if (y > margin) NewPage();
                continue;
            }
            var font = block.Kind == BlockKind.Heading ? headingFont : bodyFont;
            var lineHeight = font.GetHeight() * 1.25;
            var maxWidth = page!.Width.Point - 2 * margin;
            if (block.Kind == BlockKind.Heading && y > margin) y += lineHeight * 0.5;
            foreach (var line in Wrap(gfx!, block.Text, font, maxWidth))
            {
                if (y + lineHeight > page.Height.Point - margin) NewPage();
                gfx!.DrawString(line, font, XBrushes.Black, new XPoint(margin, y + font.GetHeight()));
                y += lineHeight;
            }
            y += lineHeight * 0.6;
        }
        gfx?.Dispose();
        document.Save(output);
    }

    private static IEnumerable<string> Wrap(XGraphics gfx, string text, XFont font, double maxWidth)
    {
        foreach (var rawLine in text.Replace("\t", "    ").Split('\n'))
        {
            var line = new StringBuilder();
            foreach (var word in rawLine.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && gfx.MeasureString(candidate, font).Width > maxWidth)
                {
                    yield return line.ToString();
                    line.Clear().Append(word);
                }
                else
                {
                    line.Clear().Append(candidate);
                }
                // Hard-break words longer than a full line.
                while (gfx.MeasureString(line.ToString(), font).Width > maxWidth && line.Length > 1)
                {
                    var cut = line.Length - 1;
                    while (cut > 1 && gfx.MeasureString(line.ToString(0, cut), font).Width > maxWidth) cut--;
                    yield return line.ToString(0, cut);
                    line.Remove(0, cut);
                }
            }
            yield return line.ToString();
        }
    }

    private static void WriteEpub(DocContent content, string output)
    {
        // Chapters start at headings or page breaks; very long runs are split for readers' sake.
        var chapters = new List<(string Title, List<DocBlock> Blocks)>();
        foreach (var block in content.Blocks)
        {
            if (chapters.Count == 0 || block.Kind == BlockKind.Heading || chapters[^1].Blocks.Count > 200 ||
                (block.Kind == BlockKind.PageBreak && chapters[^1].Blocks.Count > 40))
                chapters.Add((block.Kind == BlockKind.Heading ? block.Text : $"Part {chapters.Count + 1}", []));
            if (block.Kind != BlockKind.PageBreak) chapters[^1].Blocks.Add(block);
        }
        if (chapters.Count == 0) chapters.Add((content.Title, []));

        var id = "urn:uuid:" + Guid.NewGuid();
        using var fs = File.Create(output);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        var mimetype = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (var w = new StreamWriter(mimetype.Open(), new UTF8Encoding(false))) w.Write("application/epub+zip");

        AddText(zip, "META-INF/container.xml",
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles>
            </container>
            """);

        var manifest = new StringBuilder();
        var spine = new StringBuilder();
        var nav = new StringBuilder();
        for (var i = 0; i < chapters.Count; i++)
        {
            var name = $"chapter{i + 1:D3}.xhtml";
            manifest.AppendLine($"""    <item id="c{i + 1}" href="{name}" media-type="application/xhtml+xml"/>""");
            spine.AppendLine($"""    <itemref idref="c{i + 1}"/>""");
            nav.AppendLine($"""      <li><a href="{name}">{Esc(chapters[i].Title)}</a></li>""");

            var body = new StringBuilder();
            foreach (var block in chapters[i].Blocks)
                body.AppendLine(block.Kind == BlockKind.Heading ? $"<h2>{Esc(block.Text)}</h2>" : $"<p>{Esc(block.Text)}</p>");
            AddText(zip, "OEBPS/" + name, Xhtml(chapters[i].Title, body.ToString()));
        }

        AddText(zip, "OEBPS/nav.xhtml", Xhtml("Contents", $"<nav epub:type=\"toc\"><h1>Contents</h1><ol>\n{nav}</ol></nav>"));
        AddText(zip, "OEBPS/content.opf",
            $"""
             <?xml version="1.0" encoding="UTF-8"?>
             <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="bookid">
               <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
                 <dc:identifier id="bookid">{id}</dc:identifier>
                 <dc:title>{Esc(content.Title)}</dc:title>
                 <dc:language>{CultureInfo.CurrentUICulture.TwoLetterISOLanguageName}</dc:language>
                 <meta property="dcterms:modified">{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</meta>
               </metadata>
               <manifest>
                 <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
             {manifest}  </manifest>
               <spine>
             {spine}  </spine>
             </package>
             """);
    }

    private static string Xhtml(string title, string body) =>
        $"""
         <?xml version="1.0" encoding="UTF-8"?>
         <!DOCTYPE html>
         <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">
         <head><title>{Esc(title)}</title><style>body{"{"}font-family:serif;line-height:1.5{"}"}</style></head>
         <body>
         {body}
         </body>
         </html>
         """;

    private static void AddText(ZipArchive zip, string name, string text)
    {
        using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        w.Write(text);
    }

    private static string Esc(string s) => WebUtility.HtmlEncode(s);

    // ---------- Spreadsheets ----------

    public static List<string> XlsxToCsv(string input)
    {
        using var doc = SpreadsheetDocument.Open(input, false);
        var wb = doc.WorkbookPart ?? throw new InvalidDataException(Loc.T("Not a valid workbook."));
        var shared = wb.SharedStringTablePart?.SharedStringTable.Elements<SharedStringItem>().Select(s => s.InnerText).ToList() ?? [];
        var sheets = wb.Workbook.Sheets?.Elements<Sheet>().ToList() ?? [];
        var outputs = new List<string>();

        foreach (var sheet in sheets)
        {
            if (wb.GetPartById(sheet.Id!) is not WorksheetPart part) continue;
            var sb = new StringBuilder();
            foreach (var row in part.Worksheet.Descendants<Row>())
            {
                var cells = new List<string>();
                foreach (var cell in row.Elements<Cell>())
                {
                    var column = ColumnIndex(cell.CellReference?.Value ?? "");
                    while (column >= 0 && cells.Count < column) cells.Add("");
                    cells.Add(CsvEscape(CellText(cell, shared)));
                }
                sb.AppendLine(string.Join(",", cells));
            }
            var suffix = sheets.Count > 1 ? "- " + sheet.Name : null;
            var output = OutputPaths.For(input, "csv", suffix);
            File.WriteAllText(output, sb.ToString(), new UTF8Encoding(true));
            outputs.Add(output);
        }
        return outputs;
    }

    private static string CellText(Cell cell, List<string> shared)
    {
        var raw = cell.CellValue?.Text ?? cell.InnerText;
        if (cell.DataType?.Value == CellValues.SharedString && int.TryParse(raw, out var idx) && idx < shared.Count) return shared[idx];
        if (cell.DataType?.Value == CellValues.Boolean) return raw == "1" ? "TRUE" : "FALSE";
        return raw;
    }

    private static int ColumnIndex(string reference)
    {
        var index = 0;
        var letters = 0;
        foreach (var c in reference.TakeWhile(char.IsLetter))
        {
            index = index * 26 + (char.ToUpperInvariant(c) - 'A' + 1);
            letters++;
        }
        return letters == 0 ? -1 : index - 1;
    }

    private static string CsvEscape(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    public static void CsvToXlsx(string input, string output)
    {
        var text = ReadAllTextSmart(input);
        var firstLine = text.Split('\n')[0];
        var delimiter = FileKinds.Ext(input) == "tsv" ? '\t' : firstLine.Count(c => c == ';') > firstLine.Count(c => c == ',') ? ';' : ',';

        using var doc = SpreadsheetDocument.Create(output, SpreadsheetDocumentType.Workbook);
        var wb = doc.AddWorkbookPart();
        wb.Workbook = new Workbook();
        var ws = wb.AddNewPart<WorksheetPart>();
        var data = new SheetData();
        uint rowIndex = 1;
        foreach (var fields in ParseCsv(text, delimiter))
        {
            var row = new Row { RowIndex = rowIndex++ };
            foreach (var field in fields)
            {
                var isNumber = double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
                row.Append(isNumber
                    ? new Cell { DataType = CellValues.Number, CellValue = new CellValue(field) }
                    : new Cell { DataType = CellValues.InlineString, InlineString = new InlineString(new Text(field)) });
            }
            data.Append(row);
        }
        ws.Worksheet = new Worksheet(data);
        wb.Workbook.AppendChild(new Sheets(new Sheet { Id = wb.GetIdOfPart(ws), SheetId = 1, Name = "Sheet1" }));
    }

    private static IEnumerable<List<string>> ParseCsv(string text, char delimiter)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == delimiter) { fields.Add(field.ToString()); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                fields.Add(field.ToString());
                field.Clear();
                yield return fields;
                fields = [];
            }
            else field.Append(c);
        }
        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            yield return fields;
        }
    }

    // ---------- Apple iWork ----------

    /// <summary>Pages/Numbers/Keynote bundles carry a preview PDF or JPEG; that's what can be exported without a Mac.</summary>
    public static void ExportIWorkPreview(string input, string output)
    {
        using var zip = ZipFile.OpenRead(input);
        var pdf = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("Preview.pdf", StringComparison.OrdinalIgnoreCase));
        var jpg = zip.Entries
            .Where(e => e.Name.StartsWith("preview", StringComparison.OrdinalIgnoreCase) && e.Name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Length)
            .FirstOrDefault();
        var target = FileKinds.Ext(output);

        if (target == "pdf" && pdf is not null)
        {
            pdf.ExtractToFile(output);
        }
        else if (jpg is not null)
        {
            var tempJpg = AppPaths.NewTempFile("jpg");
            jpg.ExtractToFile(tempJpg);
            try
            {
                if (target == "pdf") PdfEngine.ImagesToPdf([tempJpg], output);
                else File.Copy(tempJpg, output);
            }
            finally
            {
                File.Delete(tempJpg);
            }
        }
        else if (pdf is not null)
        {
            var tempPdf = AppPaths.NewTempFile("pdf");
            pdf.ExtractToFile(tempPdf);
            try
            {
                var png = PdfEngine.RenderAsync(tempPdf, [0], 2000).GetAwaiter().GetResult()[0];
                using var image = new ImageMagick.MagickImage(png);
                ImageEngine.Save(image, output, target, 92);
            }
            finally
            {
                File.Delete(tempPdf);
            }
        }
        else
        {
            throw new InvalidDataException(Loc.T("This file has no embedded preview. Export it from Pages/Numbers/Keynote on a Mac."));
        }
    }

    // ---------- Pandoc (optional, better fidelity) ----------

    public static bool CanUsePandoc(string input, string target) =>
        ToolLocator.Pandoc is not null && FileKinds.Ext(input) is "docx" or "epub" or "md" && target is "docx" or "epub";

    public static async Task PandocAsync(string input, string output, CancellationToken token)
    {
        var (code, _, stderr) = await Ffmpeg.RunCaptureAsync(ToolLocator.Pandoc!, [input, "-o", output], token);
        if (code != 0) throw new InvalidOperationException(Loc.F("Pandoc failed: {0}", stderr.Trim()));
    }
}
