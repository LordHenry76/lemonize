using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Lemonize.Core;

/// <summary>
/// UI translations. English text is the key, so untranslated strings fall back to English.
/// Static window text is translated by <see cref="Apply"/>; dynamic text goes through <see cref="T"/>/<see cref="F"/>.
/// </summary>
public static class Loc
{
    public static readonly (string Code, string Name)[] Languages = [("auto", "Automatic (Windows)"), ("en", "English"), ("it", "Italiano")];

    private static readonly Dictionary<string, string> Italian = new()
    {
        // ---------- Wheel ----------
        ["Convert"] = "Converti",
        ["Tools"] = "Strumenti",
        ["Actions"] = "Azioni",
        ["Nothing here"] = "Niente qui",
        ["release to cancel"] = "rilascia per annullare",
        ["{0} items"] = "{0} elementi",
        ["Shift: Convert   ·   Alt: Tools   ·   Ctrl: Actions"] = "Shift: Converti   ·   Alt: Strumenti   ·   Ctrl: Azioni",
        ["Extract"] = "Estrai",
        ["audio only"] = "solo audio",
        ["animated"] = "animata",
        ["video"] = "video",
        ["pages"] = "pagine",
        ["text"] = "testo",
        ["preview"] = "anteprima",
        ["needs Office"] = "serve Office",
        ["Compress"] = "Comprimi",
        ["Edit"] = "Modifica",
        ["crop · adjust · annotate"] = "ritaglia · annota",
        ["Images to PDF"] = "Immagini in PDF",
        ["Scan document"] = "Scansiona documento",
        ["A4 PDF"] = "PDF A4",
        ["Collage"] = "Collage",
        ["Read QR"] = "Leggi QR",
        ["trim · crop · speed · split"] = "taglia · velocità",
        ["Join"] = "Unisci",
        ["Normalize audio"] = "Normalizza audio",
        ["trim · bleep · channels"] = "taglia · bip · canali",
        ["Normalize"] = "Normalizza",
        ["Merge"] = "Unisci",
        ["Organize"] = "Organizza",
        ["reorder · rotate · delete"] = "riordina · ruota",
        ["combine pages"] = "combina pagine",
        ["Split"] = "Dividi",
        ["first page"] = "prima pagina",
        ["Metadata"] = "Metadati",
        ["view · edit"] = "vedi · modifica",
        ["view"] = "vedi",
        ["Remove"] = "Rimuovi",
        ["all metadata"] = "tutti i metadati",
        ["Share"] = "Condividi",
        ["Float"] = "Fluttuante",
        ["Copy"] = "Copia",
        ["to clipboard"] = "negli appunti",
        ["Move to…"] = "Sposta in…",
        ["Copy path"] = "Copia percorso",
        ["Terminal"] = "Terminale",
        ["open here"] = "apri qui",

        // ---------- Jobs ----------
        ["Waiting…"] = "In attesa…",
        ["Working…"] = "In corso…",
        ["Done"] = "Fatto",
        ["{0} files created"] = "{0} file creati",
        ["Canceled"] = "Annullato",
        ["{0} → {1}"] = "{0} → {1}",
        ["Compress {0}"] = "Comprimi {0}",
        ["Normalize {0}"] = "Normalizza {0}",
        ["Remove metadata · {0}"] = "Rimuovi metadati · {0}",
        ["Split {0}"] = "Dividi {0}",
        ["Join {0} files"] = "Unisci {0} file",
        ["Merge {0} PDFs"] = "Unisci {0} PDF",
        ["{0} images → PDF"] = "{0} immagini → PDF",
        ["1 image → PDF"] = "1 immagine → PDF",
        ["Collage of {0} images"] = "Collage di {0} immagini",
        ["Edit {0}"] = "Modifica {0}",
        ["Organize {0}"] = "Organizza {0}",
        ["Combine PDFs"] = "Combina PDF",
        ["Extract pages"] = "Estrai pagine",
        ["Frame from {0}"] = "Fotogramma da {0}",
        ["Analyzing (pass 1 of 2)…"] = "Analisi (passaggio 1 di 2)…",
        ["Compressing (pass 2 of 2)…"] = "Compressione (passaggio 2 di 2)…",
        [" (smallest possible)"] = " (il minimo possibile)",

        // ---------- Output names ----------
        ["(compressed)"] = "(compresso)",
        ["(edited)"] = "(modificato)",
        ["(clean)"] = "(pulito)",
        ["(normalized)"] = "(normalizzato)",
        ["(organized)"] = "(organizzato)",
        ["(extract)"] = "(estratto)",
        ["(part {0})"] = "(parte {0})",
        ["(frame {0})"] = "(fotogramma {0})",
        ["Merged"] = "Unito",
        ["Joined"] = "Unito",
        ["Images"] = "Immagini",
        ["Archive"] = "Archivio",
        ["split"] = "divisi",

        // ---------- Dialogs & errors ----------
        ["OK"] = "OK",
        ["Cancel"] = "Annulla",
        ["Close"] = "Chiudi",
        ["Something went wrong"] = "Qualcosa è andato storto",
        ["Can't open PDF"] = "Impossibile aprire il PDF",
        ["Split PDF"] = "Dividi PDF",
        ["every page"] = "ogni pagina",
        ["Page ranges for the new files ({0} pages). Leave as is to split every page.\nExample: 1-3, 4, 5-{0}"] =
            "Intervalli di pagine per i nuovi file ({0} pagine). Lascia così per dividere ogni pagina.\nEsempio: 1-3, 4, 5-{0}",
        ["Codes found"] = "Codici trovati",
        ["No QR code or barcode found."] = "Nessun QR code o codice a barre trovato.",
        ["Error: {0}"] = "Errore: {0}",
        ["Open link"] = "Apri link",
        ["Some items were not moved"] = "Alcuni elementi non sono stati spostati",
        ["Move {0} items to…"] = "Sposta {0} elementi in…",
        ["Move 1 item to…"] = "Sposta 1 elemento in…",
        ["Quit Lemonize?"] = "Uscire da Lemonize?",
        ["Some files are still being processed. Quitting cancels them."] = "Alcuni file sono ancora in lavorazione. Uscendo verranno annullati.",
        ["Quit"] = "Esci",

        ["GZIP holds a single file. Use TAR.GZ for several files."] = "GZIP contiene un solo file. Usa TAR.GZ per più file.",
        ["Target size must be greater than zero."] = "La dimensione di destinazione deve essere maggiore di zero.",
        ["This file type can't be compressed."] = "Questo tipo di file non può essere compresso.",
        ["Can't determine the video duration."] = "Impossibile determinare la durata del video.",
        ["Target too small for a {0}s video. Try a larger size."] = "Destinazione troppo piccola per un video di {0}s. Prova una dimensione maggiore.",
        ["Can't determine the audio duration."] = "Impossibile determinare la durata dell'audio.",
        ["Ghostscript failed: {0}"] = "Ghostscript non è riuscito: {0}",
        ["This PDF is already as small as Lemonize can make it."] = "Questo PDF è già piccolo quanto Lemonize riesce a renderlo.",
        ["The document has no body."] = "Il documento non ha contenuto.",
        ["Unsupported document target."] = "Formato di destinazione non supportato.",
        ["Not a valid workbook."] = "Non è una cartella di lavoro valida.",
        ["This file has no embedded preview. Export it from Pages/Numbers/Keynote on a Mac."] =
            "Questo file non contiene un'anteprima. Esportalo da Pages/Numbers/Keynote su un Mac.",
        ["Pandoc failed: {0}"] = "Pandoc non è riuscito: {0}",
        ["Cannot read media file: {0}"] = "Impossibile leggere il file multimediale: {0}",
        ["FFmpeg failed: {0}"] = "FFmpeg non è riuscito: {0}",
        ["Could not extract a frame at this position."] = "Impossibile estrarre un fotogramma in questa posizione.",
        ["This file has no audio track."] = "Questo file non ha una traccia audio.",
        ["This file has no video track."] = "Questo file non ha una traccia video.",
        ["{0} has no video."] = "{0} non ha video.",
        ["{0} has no audio."] = "{0} non ha audio.",
        ["Removing metadata isn't supported for this file type."] = "La rimozione dei metadati non è supportata per questo tipo di file.",
        ["Not a valid JPEG file."] = "Non è un file JPEG valido.",
        ["Corrupt JPEG structure."] = "Struttura JPEG danneggiata.",
        ["This conversion needs Microsoft Office or LibreOffice (free: libreoffice.org)."] =
            "Questa conversione richiede Microsoft Office o LibreOffice (gratuito: libreoffice.it).",
        ["LibreOffice could not convert the file. {0}"] = "LibreOffice non è riuscito a convertire il file. {0}",
        ["Office could not convert the file: {0}"] = "Office non è riuscito a convertire il file: {0}",
        ["Invalid page range \"{0}\" (document has {1} pages)."] = "Intervallo di pagine non valido \"{0}\" (il documento ha {1} pagine).",
        ["Enter at least one page range."] = "Inserisci almeno un intervallo di pagine.",
        ["FFmpeg was downloaded but could not be found in the archive."] = "FFmpeg è stato scaricato ma non è stato trovato nell'archivio.",
        ["FFmpeg is not installed. Open Lemonize settings to download it."] = "FFmpeg non è installato. Scaricalo dalla finestra principale di Lemonize.",
        ["This PDF can't be opened: {0}"] = "Impossibile aprire questo PDF: {0}",

        // ---------- Tray ----------
        ["Open Lemonize"] = "Apri Lemonize",
        ["Show progress"] = "Mostra avanzamento",
        ["Drag wheel enabled"] = "Ruota di trascinamento attiva",
        ["Settings…"] = "Impostazioni…",
        ["Quit Lemonize"] = "Esci da Lemonize",
        ["drag files with Shift"] = "trascina i file con Shift",

        ["developed by"] = "sviluppato da",

        // ---------- Main window ----------
        ["Convert, compress and edit files right where you drag them."] = "Converti, comprimi e modifica i file proprio dove li trascini.",
        ["Settings"] = "Impostazioni",
        ["Drop files here"] = "Trascina qui i file",
        ["or click to choose files"] = "oppure clicca per sceglierli",
        ["Anywhere in Windows"] = "Ovunque in Windows",
        ["Start dragging files in File Explorer or on the desktop, then hold a key. A wheel opens around the pointer: drop on a choice."] =
            "Inizia a trascinare dei file in Esplora file o sul desktop, poi tieni premuto un tasto. Attorno al puntatore si apre una ruota: rilascia su una scelta.",
        ["JPEG, PNG, HEIC, MP4, MP3, PDF, DOCX, EPUB, ZIP…"] = "JPEG, PNG, HEIC, MP4, MP3, PDF, DOCX, EPUB, ZIP…",
        ["Compress, edit, trim, merge PDFs, metadata, QR…"] = "Comprimi, modifica, taglia, unisci PDF, metadati, QR…",
        ["Share, float, copy, move, copy path, terminal"] = "Condividi, fluttuante, copia, sposta, copia percorso, terminale",
        ["Converted files are saved next to the originals. Everything runs on this PC; nothing is uploaded."] =
            "I file convertiti vengono salvati accanto agli originali. Tutto avviene su questo PC: nulla viene caricato online.",
        ["Download FFmpeg"] = "Scarica FFmpeg",
        ["video & audio"] = "video e audio",
        ["DOC, XLS, PPT → PDF"] = "DOC, XLS, PPT → PDF",
        ["stronger PDF compression"] = "compressione PDF più forte",
        ["richer DOCX ↔ EPUB"] = "DOCX ↔ EPUB più fedele",
        ["Choose files for Lemonize"] = "Scegli i file per Lemonize",
        ["FFmpeg download failed"] = "Download di FFmpeg non riuscito",

        // ---------- Progress ----------
        ["Lemonize — Progress"] = "Lemonize — Avanzamento",
        ["Clear finished"] = "Rimuovi completati",
        ["Hide"] = "Nascondi",

        // ---------- Settings ----------
        ["Lemonize Settings"] = "Impostazioni di Lemonize",
        ["General"] = "Generale",
        ["Language"] = "Lingua",
        ["Automatic (Windows)"] = "Automatica (Windows)",
        ["Restart Lemonize to apply the new language."] = "Riavvia Lemonize per applicare la nuova lingua.",
        ["Open the wheel when dragging files with Shift or Ctrl"] = "Apri la ruota quando trascini file con Shift o Ctrl",
        ["Start Lemonize when I sign in"] = "Avvia Lemonize all'accesso a Windows",
        ["Add “Lemonize…” to the File Explorer right-click menu"] = "Aggiungi “Lemonize…” al menu del tasto destro di Esplora file",
        ["Show the progress window"] = "Mostra la finestra di avanzamento",
        ["Show converted files in File Explorer when done"] = "Mostra i file convertiti in Esplora file al termine",
        ["Output"] = "Salvataggio",
        ["Save next to the original file"] = "Salva accanto al file originale",
        ["Save to folder:"] = "Salva nella cartella:",
        ["Choose…"] = "Scegli…",
        ["Tools modify the original file instead of making a copy"] = "Gli strumenti modificano il file originale invece di crearne una copia",
        ["Quality"] = "Qualità",
        ["JPEG / WebP quality"] = "Qualità JPEG / WebP",
        ["Jobs at the same time"] = "Lavori in contemporanea",
        ["Browse…"] = "Sfoglia…",
        ["Leave empty to find FFmpeg automatically"] = "Lascia vuoto per trovare FFmpeg automaticamente",
        ["Save"] = "Salva",
        ["(none)"] = "(nessuna)",
        ["Using {0}"] = "In uso: {0}",
        ["FFmpeg not found. Use “Download FFmpeg” in the main window."] = "FFmpeg non trovato. Usa “Scarica FFmpeg” nella finestra principale.",
        ["Save converted files to…"] = "Salva i file convertiti in…",
        ["Locate ffmpeg.exe"] = "Trova ffmpeg.exe",
        ["Couldn't update Windows integration"] = "Impossibile aggiornare l'integrazione con Windows",

        // ---------- Float ----------
        ["Drag to drop anywhere · double-click to open · right-drag to move"] = "Trascina per rilasciare ovunque · doppio clic per aprire · trascina col tasto destro per spostare",

        // ---------- Compress ----------
        ["Reduce by a percentage"] = "Riduci di una percentuale",
        ["Target size for each file"] = "Dimensione esatta per ogni file",
        ["{0} files"] = "{0} file",
        ["Current size: {0}"] = "Dimensione attuale: {0}",
        ["Images keep their format when possible; videos become MP4 (H.264); PDFs re-compress embedded images. Results are saved as new files marked “(compressed)”."] =
            "Le immagini mantengono il formato quando possibile, i video diventano MP4 (H.264), nei PDF vengono ricompresse le immagini. I risultati sono salvati come nuovi file contrassegnati “(compresso)”.",
        ["Enter a target size greater than zero."] = "Inserisci una dimensione maggiore di zero.",
        ["Compression level"] = "Livello di compressione",
        ["Light"] = "Leggera",
        ["Medium"] = "Media",
        ["Maximum"] = "Massima",
        ["best quality"] = "qualità migliore",
        ["balanced"] = "equilibrata",
        ["smallest file"] = "file più piccolo",
        ["Compression levels are available for PDF files."] = "I livelli di compressione sono disponibili per i file PDF.",

        // ---------- Collage ----------
        ["Collage of {0} images"] = "Collage di {0} immagini",
        ["Columns"] = "Colonne",
        ["Columns · {0}"] = "Colonne · {0}",
        ["Spacing"] = "Spaziatura",
        ["Spacing · {0} px"] = "Spaziatura · {0} px",
        ["Tile size"] = "Dimensione riquadri",
        ["Small (400 px tiles)"] = "Piccola (riquadri da 400 px)",
        ["Medium (800 px tiles)"] = "Media (riquadri da 800 px)",
        ["Large (1200 px tiles)"] = "Grande (riquadri da 1200 px)",
        ["Background"] = "Sfondo",
        ["White"] = "Bianco",
        ["Black"] = "Nero",
        ["Transparent"] = "Trasparente",
        ["Format"] = "Formato",
        ["Create collage"] = "Crea collage",

        // ---------- Metadata ----------
        ["Remove all metadata"] = "Rimuovi tutti i metadati",
        ["Save changes"] = "Salva modifiche",
        ["Reading…"] = "Lettura…",
        ["Couldn't read metadata: {0}"] = "Impossibile leggere i metadati: {0}",
        ["Couldn't save metadata"] = "Impossibile salvare i metadati",
        ["Remove metadata"] = "Rimuovi metadati",
        ["File"] = "File",
        ["Image"] = "Immagine",
        ["Media"] = "Multimedia",
        ["Document"] = "Documento",
        ["Tags"] = "Tag",
        ["Attributes"] = "Attributi",
        ["Name"] = "Nome",
        ["Size"] = "Dimensione",
        ["Created"] = "Creato",
        ["Modified"] = "Modificato",
        ["Dimensions"] = "Dimensioni",
        ["Color space"] = "Spazio colore",
        ["Bit depth"] = "Profondità bit",
        ["Resolution"] = "Risoluzione",
        ["Color profile"] = "Profilo colore",
        ["GPS location"] = "Posizione GPS",
        ["Duration"] = "Durata",
        ["Bit rate"] = "Bitrate",
        ["Title"] = "Titolo",
        ["Author"] = "Autore",
        ["Subject"] = "Oggetto",
        ["Keywords"] = "Parole chiave",
        ["Creator"] = "Applicazione",
        ["Producer"] = "Produttore",
        ["Pages"] = "Pagine",
        ["PDF version"] = "Versione PDF",
        ["Description"] = "Descrizione",
        ["Category"] = "Categoria",
        ["Last modified by"] = "Ultima modifica di",
        ["Revision"] = "Revisione",
        ["Artist"] = "Artista",
        ["Copyright"] = "Copyright",
        ["Date taken"] = "Data scatto",
        ["Album"] = "Album",
        ["Album artist"] = "Artista album",
        ["Date"] = "Data",
        ["Genre"] = "Genere",
        ["Track"] = "Traccia",
        ["Comment"] = "Commento",

        // ---------- PDF organizer ----------
        ["Organize · {0}"] = "Organizza · {0}",
        ["Organize · {0} PDFs"] = "Organizza · {0} PDF",
        ["Rotate left"] = "Ruota a sinistra",
        ["Rotate right"] = "Ruota a destra",
        ["Delete"] = "Elimina",
        ["Move earlier"] = "Sposta prima",
        ["Move later"] = "Sposta dopo",
        ["Extract selected"] = "Estrai selezionate",
        ["Save PDF"] = "Salva PDF",
        ["Loading pages…"] = "Caricamento pagine…",
        ["{0} pages · drag to reorder, Delete to remove"] = "{0} pagine · trascina per riordinare, Canc per eliminare",
        ["Select the pages to extract first (Ctrl+click or Shift+click for several)."] = "Prima seleziona le pagine da estrarre (Ctrl+clic o Shift+clic per più pagine).",
        ["There are no pages left."] = "Non sono rimaste pagine.",

        // ---------- Image editor ----------
        ["Edit · {0}"] = "Modifica · {0}",
        ["Crop"] = "Ritaglia",
        ["Pen"] = "Penna",
        ["Rectangle"] = "Rettangolo",
        ["Arrow"] = "Freccia",
        ["Text"] = "Testo",
        ["Redact"] = "Oscura",
        ["Pixelate"] = "Pixela",
        ["Color"] = "Colore",
        ["Undo"] = "Annulla",
        ["Adjust"] = "Regola",
        ["Brightness"] = "Luminosità",
        ["Contrast"] = "Contrasto",
        ["Saturation"] = "Saturazione",
        ["Reset adjustments"] = "Azzera regolazioni",
        ["Transform"] = "Trasforma",
        ["Flip horizontal"] = "Rifletti orizzontalmente",
        ["Flip vertical"] = "Rifletti verticalmente",
        ["Resize (pixels)"] = "Ridimensiona (pixel)",
        ["Apply"] = "Applica",
        ["Save as"] = "Salva come",
        ["Same ({0})"] = "Uguale ({0})",
        ["Save copy"] = "Salva copia",
        ["Can't open image"] = "Impossibile aprire l'immagine",
        ["Couldn't render the image"] = "Impossibile generare l'immagine",
        ["Resize"] = "Ridimensiona",
        ["Enter a valid width and height in pixels."] = "Inserisci larghezza e altezza valide in pixel.",
        ["{0} × {1} px · crop {2} × {3}"] = "{0} × {1} px · ritaglio {2} × {3}",

        // ---------- Document scan ----------
        ["Scan document · {0}"] = "Scansiona documento · {0}",
        ["Scan document · {0} photos"] = "Scansiona documento · {0} foto",
        ["Loading…"] = "Caricamento…",
        ["Page {0} of {1}"] = "Pagina {0} di {1}",
        ["Previous"] = "Precedente",
        ["Next"] = "Successiva",
        ["Sheet"] = "Foglio",
        ["Find the sheet again"] = "Ritrova il foglio",
        ["Whole photo"] = "Foto intera",
        ["Remove page"] = "Togli pagina",
        ["Look"] = "Resa",
        ["Black and white"] = "Bianco e nero",
        ["Original"] = "Originale",
        ["Whitening"] = "Schiaritura",
        ["Result"] = "Risultato",
        ["Drag the corners or the sides · Shift keeps a side straight · Wheel: zoom · Wheel button: move · Enter: next"] = "Trascina angoli o lati · Shift tiene dritto il lato · Rotellina: zoom · Rotellina premuta: sposta · Invio: avanti",
        ["Scan 1 photo → PDF"] = "Scansione 1 foto → PDF",
        ["Scan {0} photos → PDF"] = "Scansione {0} foto → PDF",
        ["Scan"] = "Scansione",

        // ---------- Video editor ----------
        ["Edit video · {0}"] = "Modifica video · {0}",
        ["Range"] = "Intervallo",
        ["Set start  [I]"] = "Imposta inizio  [I]",
        ["Set end  [O]"] = "Imposta fine  [O]",
        ["Split here  [S]"] = "Dividi qui  [S]",
        ["Clear splits"] = "Rimuovi divisioni",
        ["Draw crop area on the video"] = "Disegna l'area di ritaglio sul video",
        ["Reset crop"] = "Azzera ritaglio",
        ["Speed & audio"] = "Velocità e audio",
        ["Remove audio"] = "Rimuovi audio",
        ["Frame"] = "Fotogramma",
        ["Save frame as PNG"] = "Salva fotogramma come PNG",
        ["Export"] = "Esporta",
        ["Splits export each part as its own file. Arrow keys step through the video; Shift+arrows jump 1 s."] =
            "Con le divisioni ogni parte viene esportata come file separato. Le frecce scorrono il video fotogramma per fotogramma; Shift+frecce salta di 1 s.",
        ["Previous frame"] = "Fotogramma precedente",
        ["Next frame"] = "Fotogramma successivo",
        ["Play in default player"] = "Riproduci nel lettore predefinito",
        ["Can't open video"] = "Impossibile aprire il video",
        ["{0} → {1}  ·  output {2}"] = "{0} → {1}  ·  risultato {2}",
        ["  ·  {0} parts"] = "  ·  {0} parti",
        ["\ncrop {0} × {1}"] = "\nritaglio {0} × {1}",

        // ---------- Audio editor ----------
        ["Edit audio · {0}"] = "Modifica audio · {0}",
        ["Selection"] = "Selezione",
        ["Keep only selection"] = "Mantieni solo la selezione",
        ["Bleep selection"] = "Copri selezione con bip",
        ["Clear bleeps"] = "Rimuovi bip",
        ["Reset range"] = "Azzera intervallo",
        ["Sound"] = "Suono",
        ["Normalize loudness (−16 LUFS)"] = "Normalizza volume (−16 LUFS)",
        ["Keep channels"] = "Mantieni canali",
        ["Mono"] = "Mono",
        ["Stereo"] = "Stereo",
        ["Swap left / right"] = "Inverti sinistra / destra",
        ["Fades"] = "Dissolvenze",
        ["Fade in"] = "Dissolvenza in entrata",
        ["Fade out"] = "Dissolvenza in uscita",
        ["▶ Play"] = "▶ Riproduci",
        ["▶ Play selection"] = "▶ Riproduci selezione",
        ["■ Stop"] = "■ Stop",
        ["Reading audio…"] = "Lettura audio…",
        ["Can't open audio"] = "Impossibile aprire l'audio",
        ["This file has no audio."] = "Questo file non ha audio.",
        ["Drag on the waveform to select. Selection → trim or bleep."] = "Trascina sulla forma d'onda per selezionare. Selezione → taglia o copri con bip.",
        ["Range {0} → {1} ({2})"] = "Intervallo {0} → {1} ({2})",
        ["  ·  selection {0} → {1}"] = "  ·  selezione {0} → {1}",
        ["  ·  {0} bleeps"] = "  ·  {0} bip",
        ["  ·  1 bleep"] = "  ·  1 bip",
    };

    public static bool IsItalian { get; private set; }

    /// <summary>Picks the language from settings ("auto" follows Windows). Call once at startup.</summary>
    public static void Initialize(string? setting)
    {
        IsItalian = setting switch
        {
            "it" => true,
            "en" => false,
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it",
        };
    }

    public static string T(string english) =>
        IsItalian && Italian.TryGetValue(english, out var it) ? it : english;

    public static string F(string english, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(english), args);

    /// <summary>Translates static text in a window: titles, labels, buttons, check boxes and tooltips.</summary>
    public static void Apply(DependencyObject root)
    {
        if (!IsItalian) return;
        switch (root)
        {
            case Window w:
                w.Title = Keep(w.Title);
                break;
            // A TextBlock's children are its inlines, handled here; editing them while the
            // logical tree is being enumerated throws, so snapshot first and don't recurse.
            case TextBlock tb when tb.Inlines.Count > 1 || tb.Inlines.FirstInline is not Run:
                foreach (var run in tb.Inlines.OfType<Run>().ToList()) run.Text = Keep(run.Text);
                TranslateTip(tb);
                return;
            case TextBlock tb:
                tb.Text = Keep(tb.Text);
                TranslateTip(tb);
                return;
            case ContentControl { Content: string s } cc:
                cc.Content = Keep(s);
                break;
        }
        TranslateTip(root);

        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>().ToList())
            Apply(child);
    }

    private static void TranslateTip(DependencyObject element)
    {
        if (element is FrameworkElement { ToolTip: string tip } fe) fe.ToolTip = Keep(tip);
    }

    /// <summary>Translates while keeping surrounding whitespace (e.g. "  Convert").</summary>
    private static string Keep(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var trimmed = text.Trim();
        if (!Italian.TryGetValue(trimmed, out var it)) return text;
        var start = text.IndexOf(trimmed, StringComparison.Ordinal);
        return text[..start] + it + text[(start + trimmed.Length)..];
    }
}
