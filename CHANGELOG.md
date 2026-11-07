# Duck Printer Manager Changelog

Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [2.0.1] - 2026-11-07

### Changed

- **Dati nella suite Duck.** Config, task, log, cache dei driver e tema ora sotto
  `%APPDATA%\duck\DuckPrinter` (prima config in `%LOCALAPPDATA%\Duck\PrinterManager`
  e tema in `%APPDATA%\Duck\theme`, condiviso con le altre app Duck).

### Added

- **Migrazione automatica dalla 2.0.0.** Al primo avvio i file di
  `%LOCALAPPDATA%\Duck\PrinterManager` (configurazione, coda dei task, log, cache dei
  driver) vengono spostati in `%APPDATA%\duck\DuckPrinter`, e i percorsi della cache
  dentro i task in coda vengono riscritti. Funziona anche se una build precedente della
  2.0.1 è già stata avviata: la coda vuota che creava viene sostituita da quella vecchia.
  Il tema eredita la scelta fatta prima.

### Fixed

- **Driver con molti file (Canon) che fallivano durante la copia.** Il driver ora viaggia
  come un unico zip, preparato dall'app in `%APPDATA%\duck\DuckPrinter\tmp` e cancellato a
  fine tentativo, e si estrae sul PC in una cartella temporanea corta e accessibile,
  `C:\Windows\Temp\DuckPM\<8 caratteri>`, al posto di `C:\ProgramData\DuckPM\job_<task>_<...>\drv`.
  Niente piu' percorsi oltre i 260 caratteri con Windows PowerShell 5.1, niente file in sola
  lettura, un solo trasferimento anche via WinRM. Se un file non si estrae l'errore dice quale
  e quanto e' lungo il suo percorso.
- **La cartella temporanea si rimuove sempre** a operazione conclusa: attributi sbloccati,
  tentativi ripetuti, `rd /s /q` come ultima risorsa. Le cartelle rimaste da tentativi
  interrotti si tolgono dopo un giorno, solo quelle create dall'app.
- Chi aveva lasciato la cartella di lavoro predefinita passa da sola a quella nuova.

## [2.0.0] - 2026-10-20

The first release of version 2: the PowerShell script `start.ps1` rewritten as a
.NET 8 application, with PowerShell kept for the commands on the PCs and a Rust
core for the task queue and everything held in memory.

### Added

- **Deploy as a wizard.** Operation, computers, driver, printer, summary, done —
  one decision per step. Two lists of PCs when installing (with and without the
  default printer, `*` in a pasted list picks the first); one list when removing.
- **Drivers read with SetupAPI**, the same engine Windows uses, with the old text
  parser as a fallback. Single `.inf` files or whole driver folders, drag and drop.
- **Remove before install**, asked in the summary: the printer on the same port,
  another address, or every network printer.
- **Starting values for the wizard** in **Impostazioni → Procedura**, for installs
  and for removals, so a deploy repeated every week is a list and a confirmation.
- **A task per PC, in order.** Tasks for the same PC run in the order they were
  created; a PC that is off is pinged again every 10 minutes, with no expiry and no
  limit on attempts.
- **Two accounts, tried in turn.** The AD account you signed in with and your
  Windows account; the next is used only on *access denied*. When both are denied
  the task says the account must be a local administrator, and signing in to AD
  retries those tasks at once.
- **Scheduled tasks** with live counters, a completion chart updated in real time,
  a log window per task, pause/resume, edit, and clean-up of duplicates.
- **MAC Auth**: search by MAC or host name (with DNS and ARP for names not in AD),
  the whole account on one card, automatic sign-in test, edit in place (a new MAC
  is also the new password), a wizard for new accounts, and deletion from the card.
- **Light and dark theme** without a flash on switching, an optional glass window
  with Windows' blur, illustrations in the sidebar, and a duck avatar dressed
  according to the first letter of the user's name.

### Changed

- The configuration file keeps the keys of version 1: an old `duck_config.json`
  next to the executable is imported on first start.
- The defaults in the code are example values (`example.local`, `OU=Printers,...`);
  the real ones are set in **Impostazioni**.

[2.0.0]: https://github.com/Zaick99/DuckPrinterManager/releases/tag/v2.0.0
