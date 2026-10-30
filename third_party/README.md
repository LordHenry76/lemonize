# Programmi inclusi

Questi programmi vengono copiati accanto a `Lemonize.exe` e inclusi nell'installer.
I file eseguibili non sono nel repository (superano i limiti di GitHub): si scaricano con

```powershell
.\tools\fetch-third-party.ps1
```

| Cartella | Programma | Licenza | Usato per |
|---|---|---|---|
| `ffmpeg/` | FFmpeg essentials (gyan.dev) | GPL | video e audio |
| `ghostscript/` | Ghostscript 10.08 (Artifex) | AGPL | compressione PDF |
| `pandoc/` | Pandoc 3.12.1 | GPL | DOCX ↔ EPUB / Markdown |

Sono compatibili con la licenza di Lemonize (GPLv3, vedi `LICENSE` e il README principale).
