# Duck Printer Manager Changelog

Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [2.0.2] - 2026-11-09

### Added

- **Automatic migration from 2.0.0.** On first start the files in
  `%LOCALAPPDATA%\Duck\PrinterManager` (settings, task queue, log, driver cache) are
  moved to `%APPDATA%\duck\DuckPrinter`, and the driver-cache paths inside queued tasks
  are rewritten, so those tasks still find their drivers. It works even if 2.0.1 has
  already been started: the empty queue 2.0.1 created is replaced by the old one. The
  theme keeps the choice made before.

### Fixed

- **Drivers with many files (Canon's, for one) failing while being copied.** A driver now
  travels as a single zip, packed by the app in `%APPDATA%\duck\DuckPrinter\tmp` and
  deleted when the attempt ends, and is unpacked on the PC into a short, accessible
  temporary folder, `C:\Windows\Temp\DuckPM\<8 characters>`, instead of
  `C:\ProgramData\DuckPM\job_<task>_<...>\drv`. No more paths over 260 characters under
  Windows PowerShell 5.1, no read-only files, a single transfer over WinRM too. When a file
  cannot be unpacked, the error names it and says how long its path is.
- **The temporary folder is always removed** when the operation ends, whether it
  succeeded or not: attributes cleared, repeated attempts, `rd /s /q` as a last resort.
  Folders left by interrupted attempts are removed after a day, and only the ones the app
  created.
- Installations still using the old default working folder switch to the new one on their
  own.
- **The task list in Scheduled tasks** now fills its panel to the bottom. A fixed 64 px
  space for the selection bar was always reserved, even with nothing selected; now it
  appears only while the bar is shown.

## [2.0.1] - 2026-10-23

### Changed

- **Dati nella suite Duck.** Config, task, log, cache dei driver e tema ora sotto
  `%APPDATA%\duck\DuckPrinter` (prima config in `%LOCALAPPDATA%\Duck\PrinterManager`
  e tema in `%APPDATA%\Duck\theme`). Nessuna migrazione automatica: i file della
  2.0.0 restano dove sono finché non li sposti a mano.

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
