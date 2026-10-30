# Lemonize

[English](README.md) · **Italiano**

Convertitore di file per Windows ispirato a *Tangerine* per macOS. Trascini file da Esplora file o dal desktop, tieni premuto un tasto e una ruota di scelte si apre attorno al puntatore: rilasci il file su una voce e il risultato viene salvato accanto all'originale. Tutto gira in locale e nessun file viene caricato online.

| Tasto durante il trascinamento | Ruota | Contenuto |
|---|---|---|
| **Shift** | Converti | JPEG, PNG, WebP, TIFF, BMP, GIF, SVG, PDF, ICO, AVIF · MP4, MOV, MKV, AVI, WMV, WebM, GIF · MP3, M4A, WAV, FLAC, OGG, Opus, AIFF · PDF, TXT, DOCX, EPUB, CSV, XLSX, PPTX · SRT, VTT · ZIP, TAR, TAR.GZ, GZIP, estrai RAR/7z |
| **Alt** | Strumenti | Comprimi (a percentuale o a dimensione esatta), editor immagini (ritaglio, regolazioni, annotazioni, oscuramento, pixelatura), editor video (taglio, ritaglio, velocità, divisione, fotogramma), editor audio (taglio, bip, normalizzazione, canali, dissolvenze), unisci, PDF (unisci, dividi, riordina/ruota/elimina pagine, immagini→PDF), scansione documenti (foto di fogli raddrizzate e pulite in PDF A4; documenti d'identità con fronte e retro a grandezza reale su un A4), collage, metadati (vedi/modifica/rimuovi), lettura QR/codici a barre |
| **Ctrl** | Azioni | Condividi (menu Condividi di Windows), Float (file fluttuanti), Copia, Sposta in…, Copia percorso, Terminale |

Altri modi di usarla: la finestra principale (trascina oppure clicca per scegliere i file) e, se attivato nelle impostazioni, la voce **Lemonize…** nel menu del tasto destro di Esplora file. La ruota si usa anche con il mouse (clic) e con Tab/Esc.

## Lingue

Italiano e inglese. Di default la lingua segue quella di Windows. Si può forzare in *Impostazioni → Lingua*; la modifica vale dal riavvio successivo.
Le traduzioni sono in `Core/Loc.cs`: il testo inglese fa da chiave e c'è un dizionario italiano. I testi fissi delle finestre vengono tradotti in automatico all'apertura (`Loc.Apply`); quelli dinamici passano da `Loc.T`/`Loc.F`. Per aggiungere una lingua basta aggiungere un dizionario.

## Requisiti

- Windows 10 2004 (19041) o successivo, x64.
- FFmpeg, Ghostscript e Pandoc sono inclusi (cartelle accanto a `Lemonize.exe`).
- Facoltativi:
  - **Microsoft Office** o **LibreOffice** per DOC/XLS/PPT/ODT → PDF e tra formati Office.

## Struttura

```
src/Lemonize/
  App.xaml(.cs)        avvio, istanza singola (named pipe), icona nell'area di notifica, hook globale del mouse
  Core/                impostazioni, tipi di file, catalogo conversioni (Converter), comandi delle ruote,
                       percorsi di output, integrazione con Windows, installer di FFmpeg
  Engines/             Image (Magick.NET), Media/MediaTools/Ffmpeg, Pdf (PDFsharp, PdfPig, Windows.Data.Pdf),
                       Document (OpenXML, EPUB), Office (COM/LibreOffice), Archive, Subtitle,
                       Metadata, Compression, Qr (ZXing), Scan (scansione di fogli e documenti d'identità)
  Interop/             hook del mouse, miniature di Esplora file, menu Condividi, P/Invoke
  Jobs/                coda dei lavori con avanzamento e annullamento
  UI/                  ruota, finestra principale, avanzamento, impostazioni, editor e dialoghi
tests/Lemonize.Smoke/  test end-to-end: genera file di esempio ed esegue ogni conversione e strumento
third_party/ghostscript/ Ghostscript 10.08 (solo gswin64c.exe + gsdll64.dll), copiato accanto a Lemonize.exe
third_party/pandoc/     Pandoc 3.12.1 (pandoc.exe), copiato accanto a Lemonize.exe
third_party/ffmpeg/     FFmpeg 9.0 essentials (ffmpeg.exe + ffprobe.exe), copiato accanto a Lemonize.exe
installer/             script Inno Setup per Lemonize-Setup.exe
```

### Come funziona la ruota

Un hook a basso livello (`WH_MOUSE_LL`) si accorge che stai trascinando con Shift, Alt o Ctrl premuto (non si usano Shift+Alt né Ctrl+Shift, che in Windows cambiano la lingua di input). A quel punto sotto il puntatore compare una finestra trasparente che fa da *drop target*. Diventa visibile solo quando Windows le consegna davvero dei file (`DragEnter` con `FileDrop`), così trascinare testo o selezionare con Shift non mostra nulla. Il rilascio su un settore avvia il comando. Uscire dalla ruota o rilasciare altrove la chiude.

## Sviluppo

```powershell
# una volta sola dopo il clone: scarica FFmpeg, Ghostscript e Pandoc in third_party\ (serve 7-Zip)
.\tools\fetch-third-party.ps1

dotnet build Lemonize.sln
dotnet run --project src/Lemonize

# test end-to-end (serve FFmpeg)
dotnet run --project tests/Lemonize.Smoke -- C:\temp\lemonize-smoke
# aggiungi --render-wheels per salvare immagini PNG delle ruote, --italian per provare in italiano

# mockup per il sito: finestre e ruote vere, in italiano, PNG trasparenti in mockups\ (non versionata)
dotnet run --project tools/Lemonize.Mockups

# installer: dist\Lemonize-Setup-<versione>.exe (serve Inno Setup 6: winget install JRSoftware.InnoSetup --scope user)
.\publish.ps1

# release su GitHub: tag v<versione> + installer, note prese dal commit "(<versione>)" già pushato (serve gh)
.\release.ps1
```

I commit di una versione si scrivono in inglese, poi una riga con `---`, poi lo stesso in italiano:
`release.ps1` li trasforma in note di release in entrambe le lingue.


## Licenza

Lemonize è software libero distribuito con licenza **GNU General Public License v3.0** (vedi [LICENSE](LICENSE)): puoi usarlo, studiarlo, modificarlo e condividerlo, a patto che le versioni modificate che distribuisci restino sotto la stessa licenza e con il codice sorgente.

### Programmi e librerie inclusi

L'installer contiene **FFmpeg** (GPL, build "essentials"), **Pandoc** (GPL) e **Ghostscript** (AGPL). Funzionano come programmi separati e il loro codice sorgente è disponibile presso i rispettivi autori; i testi delle loro licenze sono in `third_party/`.


Magick.NET (Apache 2.0), PdfPig (Apache 2.0), ZXing.Net (Apache 2.0), DocumentFormat.OpenXml (MIT), SharpCompress (MIT), **PDFsharp 6.2.4 (MIT)**: le versioni dalla 6.5 in poi sono AGPL, quindi non aggiornarla senza una licenza commerciale.

## Limiti noti

- **HEIC** si legge (es. foto iPhone → JPEG) ma non si scrive: le foto HEIC compresse o modificate vengono salvate in JPEG.
- **Pages/Numbers/Keynote**: viene esportata solo l'anteprima incorporata nel file (PDF o JPEG).
- **PDF → DOCX/EPUB/TXT** estrae il testo, non l'impaginazione.
- Le conversioni Office passano da Word/Excel/PowerPoint (automazione COM) o da LibreOffice.
- L'installer non è firmato: al primo avvio Windows mostra "PC protetto da Windows" (*Ulteriori informazioni → Esegui comunque*).
