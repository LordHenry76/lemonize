using System.IO;
using System.Runtime.InteropServices;
using Lemonize.Core;

namespace Lemonize.Engines;

/// <summary>Converts Office documents using Microsoft Office (COM automation) or LibreOffice, whichever is installed.</summary>
public static class OfficeEngine
{
    public static bool IsAvailable(FileKind kind) => ToolLocator.LibreOffice is not null || kind switch
    {
        FileKind.WordDocument or FileKind.Text => ToolLocator.HasWord,
        FileKind.Spreadsheet or FileKind.Csv => ToolLocator.HasExcel,
        FileKind.Presentation => ToolLocator.HasPowerPoint,
        _ => false,
    };

    public static async Task ConvertAsync(string input, string output, CancellationToken token)
    {
        var kind = FileKinds.Detect(input);
        var target = FileKinds.Ext(output);
        var temp = AppPaths.NewTempFile(target);
        try
        {
            if (HasMicrosoftOffice(kind)) await RunStaAsync(() => ConvertWithOffice(kind, input, temp, target));
            else if (ToolLocator.LibreOffice is not null) await ConvertWithLibreOfficeAsync(input, temp, target, token);
            else throw new InvalidOperationException(Loc.T("This conversion needs Microsoft Office or LibreOffice (free: libreoffice.org)."));
            File.Move(temp, output);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static bool HasMicrosoftOffice(FileKind kind) => kind switch
    {
        FileKind.WordDocument or FileKind.Text => ToolLocator.HasWord,
        FileKind.Spreadsheet or FileKind.Csv => ToolLocator.HasExcel,
        FileKind.Presentation => ToolLocator.HasPowerPoint,
        _ => false,
    };

    private static void ConvertWithOffice(FileKind kind, string input, string output, string target)
    {
        input = Path.GetFullPath(input);
        switch (kind)
        {
            case FileKind.WordDocument or FileKind.Text:
            {
                dynamic word = Activator.CreateInstance(Type.GetTypeFromProgID("Word.Application")!)!;
                try
                {
                    word.Visible = false;
                    word.DisplayAlerts = 0;
                    dynamic doc = word.Documents.Open(input, false, true, false);
                    // wdFormatPDF = 17, wdFormatDocumentDefault = 16, wdFormatUnicodeText = 7, wdFormatRTF = 6
                    int format = target switch { "pdf" => 17, "docx" => 16, "txt" => 7, "rtf" => 6, _ => 16 };
                    doc.SaveAs2(output, format);
                    doc.Close(false);
                    Marshal.FinalReleaseComObject(doc);
                }
                finally
                {
                    word.Quit(false);
                    Marshal.FinalReleaseComObject(word);
                }
                break;
            }
            case FileKind.Spreadsheet or FileKind.Csv:
            {
                dynamic excel = Activator.CreateInstance(Type.GetTypeFromProgID("Excel.Application")!)!;
                try
                {
                    excel.Visible = false;
                    excel.DisplayAlerts = false;
                    dynamic wb = excel.Workbooks.Open(input, 0, true);
                    if (target == "pdf") wb.ExportAsFixedFormat(0, output);
                    else wb.SaveAs(output, target == "csv" ? 6 : 51); // xlCSV = 6, xlOpenXMLWorkbook = 51
                    wb.Close(false);
                    Marshal.FinalReleaseComObject(wb);
                }
                finally
                {
                    excel.Quit();
                    Marshal.FinalReleaseComObject(excel);
                }
                break;
            }
            case FileKind.Presentation:
            {
                dynamic ppt = Activator.CreateInstance(Type.GetTypeFromProgID("PowerPoint.Application")!)!;
                try
                {
                    // ReadOnly = msoTrue (-1), Untitled = msoFalse, WithWindow = msoFalse
                    dynamic pres = ppt.Presentations.Open(input, -1, 0, 0);
                    pres.SaveAs(output, target == "pdf" ? 32 : 24); // ppSaveAsPDF = 32, ppSaveAsOpenXMLPresentation = 24
                    pres.Close();
                    Marshal.FinalReleaseComObject(pres);
                }
                finally
                {
                    ppt.Quit();
                    Marshal.FinalReleaseComObject(ppt);
                }
                break;
            }
            default:
                throw new NotSupportedException();
        }
    }

    private static async Task ConvertWithLibreOfficeAsync(string input, string output, string target, CancellationToken token)
    {
        var outDir = AppPaths.NewTempDir();
        var profile = new Uri(Path.Combine(AppPaths.LocalData, "lo-profile")).AbsoluteUri;
        try
        {
            var filter = target == "csv" ? "csv:Text - txt - csv (StarCalc):44,34,76" : target;
            var (code, _, stderr) = await Ffmpeg.RunCaptureAsync(ToolLocator.LibreOffice!,
                ["-env:UserInstallation=" + profile, "--headless", "--norestore", "--convert-to", filter, "--outdir", outDir, input], token);
            var produced = Directory.EnumerateFiles(outDir).FirstOrDefault();
            if (code != 0 || produced is null) throw new InvalidOperationException(Loc.F("LibreOffice could not convert the file. {0}", stderr.Trim()));
            File.Move(produced, output, overwrite: true);
        }
        finally
        {
            Directory.Delete(outDir, true);
        }
    }

    private static Task RunStaAsync(Action action)
    {
        var tcs = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex is COMException ? new InvalidOperationException(Loc.F("Office could not convert the file: {0}", ex.Message), ex) : ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }
}
