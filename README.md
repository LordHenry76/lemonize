# Lemonize

**English** · [Italiano](README.it.md)

A file converter for Windows inspired by *Tangerine* for macOS. Drag files from File Explorer or the desktop, hold a key and a wheel of choices opens around the pointer: drop the file on an item and the result is saved next to the original. Everything runs locally and no file is ever uploaded.

| Key while dragging | Wheel | Contents |
|---|---|---|
| **Shift** | Convert | JPEG, PNG, WebP, TIFF, BMP, GIF, SVG, PDF, ICO, AVIF · MP4, MOV, MKV, AVI, WMV, WebM, GIF · MP3, M4A, WAV, FLAC, OGG, Opus, AIFF · PDF, TXT, DOCX, EPUB, CSV, XLSX, PPTX · SRT, VTT · ZIP, TAR, TAR.GZ, GZIP, extract RAR/7z |
| **Alt** | Tools | Compress (by percentage or to an exact size), image editor (crop, adjustments, annotations, redaction, pixelation), video editor (trim, crop, speed, split, frame grab), audio editor (trim, bleep, normalize, channels, fades), join, PDF (merge, split, reorder/rotate/delete pages, images→PDF), document scanning (photos of paper sheets straightened and cleaned into an A4 PDF; ID cards with front and back at real size on one A4 page), collage, metadata (view/edit/remove), QR/barcode reading |
| **Ctrl** | Actions | Share (Windows Share menu), Float (floating files), Copy, Move to…, Copy path, Terminal |

Other ways to use it: the main window (drag files onto it or click to pick them) and, when enabled in the settings, the **Lemonize…** entry in File Explorer's right-click menu. The wheel also works with the mouse (click) and with Tab/Esc.

## Languages

English and Italian. By default the language follows Windows; it can be forced in *Settings → Language* and the change applies from the next restart.
Translations live in `Core/Loc.cs`: the English text is the key and there is an Italian dictionary. Static window text is translated automatically when a window opens (`Loc.Apply`); dynamic text goes through `Loc.T`/`Loc.F`. Adding a language only takes another dictionary.

## Requirements

- Windows 10 2004 (19041) or later, x64.
- FFmpeg, Ghostscript and Pandoc are bundled (folders next to `Lemonize.exe`).
- Optional:
  - **Microsoft Office** or **LibreOffice** for DOC/XLS/PPT/ODT → PDF and between Office formats.

## Layout

```
src/Lemonize/
  App.xaml(.cs)        startup, single instance (named pipe), notification-area icon, global mouse hook
  Core/                settings, file kinds, conversion catalog (Converter), wheel commands,
                       output paths, Windows integration, FFmpeg installer
  Engines/             Image (Magick.NET), Media/MediaTools/Ffmpeg, Pdf (PDFsharp, PdfPig, Windows.Data.Pdf),
                       Document (OpenXML, EPUB), Office (COM/LibreOffice), Archive, Subtitle,
                       Metadata, Compression, Qr (ZXing), Scan (document and ID card scanning)
  Interop/             mouse hook, File Explorer thumbnails, Share menu, P/Invoke
  Jobs/                job queue with progress and cancellation
  UI/                  wheel, main window, progress, settings, editors and dialogs
tests/Lemonize.Smoke/  end-to-end tests: generates sample files and runs every conversion and tool
third_party/ghostscript/ Ghostscript 10.08 (gswin64c.exe + gsdll64.dll only), copied next to Lemonize.exe
third_party/pandoc/     Pandoc 3.12.1 (pandoc.exe), copied next to Lemonize.exe
third_party/ffmpeg/     FFmpeg 9.0 essentials (ffmpeg.exe + ffprobe.exe), copied next to Lemonize.exe
installer/             Inno Setup script for Lemonize-Setup.exe
```

### How the wheel works

A low-level hook (`WH_MOUSE_LL`) notices that you are dragging with Shift, Alt or Ctrl held down (Shift+Alt and Ctrl+Shift are not used, since Windows uses them to switch the input language). A transparent window then appears under the pointer as a *drop target*. It only becomes visible once Windows actually hands it files (`DragEnter` with `FileDrop`), so dragging text or selecting with Shift shows nothing. Dropping on a sector runs the command; leaving the wheel or dropping elsewhere closes it.

## Development

```powershell
# once after cloning: downloads FFmpeg, Ghostscript and Pandoc into third_party\ (needs 7-Zip)
.\tools\fetch-third-party.ps1

dotnet build Lemonize.sln
dotnet run --project src/Lemonize

# end-to-end tests (needs FFmpeg)
dotnet run --project tests/Lemonize.Smoke -- C:\temp\lemonize-smoke
# add --render-wheels to save PNG images of the wheels, --italian to test in Italian

# mockups for the website: the real windows and wheels, in Italian, as transparent PNGs in mockups\ (not versioned)
dotnet run --project tools/Lemonize.Mockups

# installer: dist\Lemonize-Setup-<version>.exe (needs Inno Setup 6: winget install JRSoftware.InnoSetup --scope user)
.\publish.ps1

# GitHub release: tag v<version> + installer, notes taken from the already pushed "(<version>)" commit (needs gh)
.\release.ps1
```

Commit messages for a version are written in English, then a line with `---`, then the same in Italian:
`release.ps1` turns them into bilingual release notes.

## License

Lemonize is free software released under the **GNU General Public License v3.0** (see [LICENSE](LICENSE)): you can use, study, modify and share it, as long as modified versions you distribute stay under the same license with their source code.

### Bundled programs and libraries

The installer includes **FFmpeg** (GPL, "essentials" build), **Pandoc** (GPL) and **Ghostscript** (AGPL). They run as separate programs, and their source code is available from their authors; their license texts are in `third_party/`.

Magick.NET (Apache 2.0), PdfPig (Apache 2.0), ZXing.Net (Apache 2.0), DocumentFormat.OpenXml (MIT), SharpCompress (MIT), **PDFsharp 6.2.4 (MIT)**: versions from 6.5 on are AGPL, so don't upgrade it without a commercial license.

## Known limits

- **HEIC** can be read (e.g. iPhone photos → JPEG) but not written: compressed or edited HEIC photos are saved as JPEG.
- **Pages/Numbers/Keynote**: only the preview embedded in the file (PDF or JPEG) is exported.
- **PDF → DOCX/EPUB/TXT** extracts the text, not the layout.
- Office conversions go through Word/Excel/PowerPoint (COM automation) or LibreOffice.
- The installer is not signed: on first launch Windows shows "Windows protected your PC" (*More info → Run anyway*).
