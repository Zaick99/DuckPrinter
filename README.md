# Duck Printer Manager 2

Riscrittura di `start.ps1` (Duck Printer Manager) come applicazione **.NET 8 / WPF**, con i comandi di
sistema eseguiti in **PowerShell** e lo stato in memoria gestito da un core **Rust** (`duck_core`).
Grafica: kit **duck-theme** (giorno/notte, vetro, tappeto di paperelle).

```
┌──────────── WPF (.NET 8) ─────────────┐     P/Invoke (C ABI, JSON)    ┌──────── duck_core (Rust) ────────┐
│ Deploy · Task · MAC Auth · Log · Impo. │ ───────────────────────────▶ │ coda task + scheduler per host   │
│ Scheduler (loop 1 s, N in parallelo)   │                              │ ring buffer log (globale/task)   │
│ InfParser (SetupAPI) · DriverCache     │                              │ segreti di sessione azzerati     │
└───────────────┬────────────────────────┘                              │ parsing host / MAC               │
                │ processo pwsh/powershell (stdin base64 JSON, ##LOG / ##RESULT)  └──────────────────────────────────┘
                ▼
   HostWorker.ps1 ──WinRM──▶ RemoteDeploy.ps1 / RemoteRemove.ps1 (sul PC)   Ad.ps1 (RSAT ActiveDirectory + ADSI)
               └──WMI/DCOM + SMB (fallback)
```

## Funzioni

### Deploy stampanti
- **Elenco host incollato** (spazi, righe, virgole, `;`): validato e deduplicato dal core Rust; caricabile da file.
- **INF a prova di errore**: lettura con **SetupAPI** (`SetupOpenInfFile`/`SetupGetStringField`, lo stesso motore di
  Windows: codifiche, righe continuate, `[Strings]` localizzate, decorazioni `NTamd64.x`); se SetupAPI rifiuta il file
  si passa al parser testuale (porting del vecchio `Parse-InfDrivers`). Singoli file o intera cartella pacchetto.
- **Nome stampante** di default `{DRIVERNAME}`; tutto il resto resta: `{DRIVERNAME} (PR0042)`. Anche `{IP}`, `{DATE}`.
- **Predefinita**: *Automatica* (set_prt se presente, altrimenti Windows), *solo set_prt*, *solo Windows*, *entrambe*, *nessuna*.
  set_prt viene aggiornato in **tutte le versioni**: `printui /y /n "..."` in qualunque ordine, `SetDefaultPrinter` (VBS/PS),
  `printers.cfg`/`.ini` (sezione con `default=1`). Backup `*.duck.bak`, codifica originale preservata.
  La predefinita di Windows si imposta nella sessione dell'utente collegato (task pianificato effimero).
- **Disinstallazione** di tutte le stampanti collegate a una porta che punta all'IP/hostname/FQDN indicato
  (`IP_x`, `x_1`, FQDN, URL IPP...). Confronto per uguaglianza: `10.1.1.5` non tocca `10.1.1.50`.
- **Ordine dei clic** rispettato per PC: Disinstalla poi Installa ⇒ l'installazione parte solo a rimozione conclusa (FIFO per host nel core).
- **Failover**: trasporto WinRM → WMI/DCOM(+SMB); copia driver SMB → `Copy-Item -ToSession`;
  driver: pnputil+DriverStore → `Add-PrinterDriver -InfPath` → `printui /ia` → pnputil `/subdirs` → DISM;
  porta: `Add-PrinterPort` → WMI; coda: `Add-Printer` → `printui /if` → WMI `Win32_Printer`.
- **PC spenti**: il task resta in coda e il PC viene ripingato ogni **10 minuti** (configurabile), **senza scadenza né
  numero massimo di tentativi**. Ping ICMP con ripiego TCP 445/5985/135. Avvio differibile (tra 30 min, stasera...).

### MAC Auth
- Login AD: se l'account ha diritti sulla OU stampanti (ACL `CreateChild`/`WriteProperty`/`GenericAll` sui SID di
  `tokenGroups`, o gruppi amministrativi) abilita **modifica/creazione**, altrimenti **sola lettura**.
- Ricerca per **MAC** (qualunque formato) o **nome host**; se il nome non e' in AD: DNS → ping → **ARP** → MAC → utente.
- Scheda completa (SAM, UPN, DN, OU, reparto, stato, blocco, gruppi e gruppi mancanti...) e **prova automatica di login**
  (bind LDAP sul DC scelto con utente=MAC/password=MAC; codici Win32 → password errata/disabilitato/bloccato/...).
  La prova viene saltata se l'utente e' a un tentativo dalla soglia di blocco.
- **Modifica**: MAC sempre minuscolo e senza separatori (filtro in digitazione nel core Rust), propagato su tutti gli
  attributi (logica originale), SAM/UPN allineati, password reimpostata; le operazioni usano l'**objectGUID** (stabile
  anche dopo la rinomina del CN) — questo risolve i problemi del vecchio `-Identity $newMAC`.
- **Creazione**: nome `PR{INCREMENTAL}` ricalcolato a ogni tentativo, controllo MAC/nome duplicati, gruppi con esito per gruppo.
- **Reparti** nella descrizione AD dall'elenco su share, con suggerimenti e (opzionale) solo valori in elenco.

### Task programmati
Griglia live, filtri, ricerca; verifica immediata, pausa/riprendi (singoli o scheduler), modifica, interrompi,
elimina, svuota (completati, scaduti, in errore, **duplicati**, tutto). Doppio clic ⇒ **finestra log del task** in tempo reale.

### Impostazioni
Dominio, OU, **DC per le modifiche** (elenco da AD), schema nome `PR{INCREMENTAL}` / `{INCREMENTAL:5}`, cifre,
password temporanea (DPAPI), **gruppi AD** (aggiungi/rimuovi/cerca), file reparti, prova login, set_prt (cartelle, batch,
processo), cartella remota, cache driver, credenziali per il deploy, **max task in parallelo**, intervallo PC spenti,
backoff errori, scadenza (0 = mai), timeout, ping TCP, avvio scheduler, tema, paperelle, percorso PowerShell.

> **Valori di esempio.** I predefiniti nel codice sono fittizi (`example.local`, `OU=Printers,...,DC=example,DC=local`,
> gruppo `MAC-Auth Printers`, `\\fileserver\share\config\reparti.txt`, processo `set_prt_agent`): al primo avvio
> impostare i valori reali in **Impostazioni**. Vengono salvati in `duck_config.json` e non finiscono nel repository.

## Requisiti
- Windows 10/11 x64, **.NET 8 Desktop Runtime** (oppure build `-SelfContained`).
- PowerShell 7 consigliato (altrimenti Windows PowerShell 5.1).
- MAC Auth: **RSAT Active Directory** (`Add-WindowsCapability -Online -Name Rsat.ActiveDirectory.DS-LDS.Tools~~~~0.0.1.0`).
- PC di destinazione: WinRM **oppure** share `C$` + DCOM/WMI; diritti di amministratore locale.

## Build
```powershell
.\build.ps1                  # cargo build/test + dotnet test + publish in .\dist
.\build.ps1 -SelfContained   # runtime .NET incluso
```
Da Linux: `./build.sh` (target `x86_64-pc-windows-gnu` + mingw).

## Dati
`%LOCALAPPDATA%\Duck\PrinterManager\`: `duck_config.json` (stesse chiavi dello script originale: un vecchio
`duck_config.json` accanto all'exe viene importato), `duck_tasks.json` (+ `.bak`, scrittura atomica),
`duck_printer_manager.log` (rotazione 5 MB), `_driver_cache\`, `ps\` (script estratti). Tema: `%APPDATA%\Duck\theme`.

## Test
- `cargo test` (19): FIFO per host, ricontrollo 10 min infinito, backoff con tetto, duplicati, pausa, scadenza, persistenza/.bak, segreti.
- `dotnet test` (16): FFI reale verso la libreria Rust, naming, parser INF (UTF-16, stringhe, continuazioni), cache driver,
  migrazione config, protocollo .NET ⇄ PowerShell end-to-end.
- Logica set_prt e matching porte verificata eseguendo gli script con pwsh.

## Licenza
MIT, vedi [LICENSE](LICENSE). Grafica (papera, illustrazioni, avatar) di Zaick.
