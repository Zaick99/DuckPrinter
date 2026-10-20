param([string]$CfgJson)
# =====================================================================================
#  Eseguito SUL PC DI DESTINAZIONE (WinRM oppure processo avviato via WMI/DCOM).
#  Deve restare compatibile con Windows PowerShell 5.1: niente operatori ?: ?? ecc.
#  Restituisce SEMPRE una stringa JSON: { ok, message, driverMethod, portMethod,
#  printerMethod, defaultApplied, log[] } - non lancia eccezioni verso il chiamante.
# =====================================================================================

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'
$Script:DuckLog = New-Object System.Collections.ArrayList
function L([string]$m) { [void]$Script:DuckLog.Add(('{0:HH:mm:ss} {1}' -f (Get-Date), $m)) }

# ---------------------------------------------------------------- testo con codifica preservata
function Read-DuckText([string]$Path) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        $enc = New-Object System.Text.UTF8Encoding($true); $off = 3
    } elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) {
        $enc = New-Object System.Text.UnicodeEncoding($false, $true); $off = 2
    } elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF) {
        $enc = New-Object System.Text.UnicodeEncoding($true, $true); $off = 2
    } else {
        $cp = 1252
        try { $cp = [System.Globalization.CultureInfo]::CurrentCulture.TextInfo.ANSICodePage } catch { }
        try { $enc = [System.Text.Encoding]::GetEncoding($cp) } catch { $enc = [System.Text.Encoding]::GetEncoding(28591) }
        $off = 0
    }
    return @{ Text = $enc.GetString($bytes, $off, $bytes.Length - $off); Encoding = $enc }
}

function Write-DuckText([string]$Path, [string]$Text, $Encoding) {
    $pre  = $Encoding.GetPreamble()
    $body = $Encoding.GetBytes($Text)
    $all  = New-Object byte[] ($pre.Length + $body.Length)
    [System.Array]::Copy($pre, 0, $all, 0, $pre.Length)
    [System.Array]::Copy($body, 0, $all, $pre.Length, $body.Length)
    [System.IO.File]::WriteAllBytes($Path, $all)
}

# ---------------------------------------------------------------- set_prt: tutte le versioni
# Script (bat/cmd/vbs/ps1): la stampante predefinita compare come
#   rundll32 printui.dll,PrintUIEntry /y /n "NOME"     (anche /n "NOME" /y, /n"NOME")
#   WshNetwork.SetDefaultPrinter "NOME"  /  SetDefaultPrinter("NOME")
# Configurazioni (cfg/ini): sezione [NOME] con default=1.
function Set-DuckDefaultInScript([string]$Text, [string]$PrinterName) {
    $pn = $PrinterName.Replace('$', '$$')
    $t = [regex]::Replace($Text, '(?im)(PrintUIEntry\b[^\r\n]*?/y\b[^\r\n]*?/n\s*)"[^"\r\n]*"', ('${1}"' + $pn + '"'))
    $t = [regex]::Replace($t,    '(?im)(PrintUIEntry\b[^\r\n]*?/n\s*)"[^"\r\n]*"([^\r\n]*?/y\b)', ('${1}"' + $pn + '"${2}'))
    $t = [regex]::Replace($t,    '(?im)(SetDefaultPrinter\s*\(?\s*)"[^"\r\n]*"', ('${1}"' + $pn + '"'))
    return $t
}

function Set-DuckDefaultInIni([string]$Text, [string]$PrinterName) {
    $nl = "`r`n"; if ($Text -notmatch "`r`n") { $nl = "`n" }
    $lines = New-Object System.Collections.ArrayList
    foreach ($l in ($Text -split "\r?\n")) { [void]$lines.Add($l) }

    # mappa sezioni: nome -> indice header; e quali hanno default=1
    $sections = @()
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*\[([^\]]+)\]\s*$') { $sections += ,@($i, $matches[1].Trim()) }
    }
    if ($sections.Count -eq 0) { return $Text }

    function Get-SectionRange($idx) {
        $start = $sections[$idx][0]
        $end = $lines.Count - 1
        if ($idx + 1 -lt $sections.Count) { $end = $sections[$idx + 1][0] - 1 }
        return @($start, $end)
    }
    function Find-DefaultLine($idx) {
        $r = Get-SectionRange $idx
        for ($j = $r[0] + 1; $j -le $r[1]; $j++) { if ($lines[$j] -match '^\s*default\s*=') { return $j } }
        return -1
    }

    $target = -1; $defaults = @()
    for ($s = 0; $s -lt $sections.Count; $s++) {
        if ($sections[$s][1] -ieq $PrinterName) { $target = $s }
        $dl = Find-DefaultLine $s
        if ($dl -ge 0 -and $lines[$dl] -match '^\s*default\s*=\s*1\s*$') { $defaults += $s }
    }

    if ($target -lt 0) {
        if ($defaults.Count -eq 0) { return $Text }   # nessun riferimento da spostare
        # come lo script originale: la sezione predefinita prende il nome della nuova stampante
        $target = $defaults[0]
        $h = $sections[$target][0]
        $lines[$h] = $lines[$h] -replace '\[[^\]]+\]', ('[' + $PrinterName + ']')
    }

    for ($s = 0; $s -lt $sections.Count; $s++) {
        $dl = Find-DefaultLine $s
        if ($s -eq $target) {
            if ($dl -ge 0) { $lines[$dl] = ($lines[$dl] -replace '=\s*\S*\s*$', '=1') }
            else {
                $r = Get-SectionRange $s
                $lines.Insert($r[0] + 1, 'default=1')
                # gli indici successivi slittano di una riga
                for ($k = $s + 1; $k -lt $sections.Count; $k++) { $sections[$k][0]++ }
            }
        } elseif ($dl -ge 0 -and $lines[$dl] -match '=\s*1\s*$') {
            $lines[$dl] = ($lines[$dl] -replace '=\s*1\s*$', '=0')
        }
    }
    return ($lines -join $nl)
}

function Update-DuckSetPrt([string[]]$Paths, [string]$PrinterName) {
    $changed = @()
    foreach ($root in $Paths) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        $files = Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction SilentlyContinue |
                 Where-Object { $_.Extension -match '^\.(bat|cmd|vbs|ps1|cfg|ini)$' }
        foreach ($f in $files) {
            try {
                $doc = Read-DuckText $f.FullName
                if ($f.Extension -match '^\.(cfg|ini)$') { $new = Set-DuckDefaultInIni $doc.Text $PrinterName }
                else { $new = Set-DuckDefaultInScript $doc.Text $PrinterName }
                if ($new -cne $doc.Text) {
                    $bak = $f.FullName + '.duck.bak'
                    if (-not (Test-Path -LiteralPath $bak)) { Copy-Item -LiteralPath $f.FullName -Destination $bak -Force }
                    Write-DuckText $f.FullName $new $doc.Encoding
                    $changed += $f.FullName
                }
            } catch { L "set_prt: $($f.FullName) non aggiornato - $($_.Exception.Message)" }
        }
    }
    return ,$changed
}

# ---------------------------------------------------------------- esecuzione nella sessione utente
function Invoke-DuckAsLoggedOnUser([string]$Execute, [string]$Arguments, [string]$Tag, [string]$RunLevel = 'Limited', [int]$WaitSec = 20) {
    $cs = Get-CimInstance -ClassName Win32_ComputerSystem -ErrorAction SilentlyContinue
    $user = $null
    if ($cs) { $user = $cs.UserName }
    if (-not $user) {
        # sessioni RDP / utente non visibile in Win32_ComputerSystem: proprietario di explorer.exe
        $exp = Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($exp) { $o = Invoke-CimMethod -InputObject $exp -MethodName GetOwner -ErrorAction SilentlyContinue; if ($o -and $o.User) { $user = "$($o.Domain)\$($o.User)" } }
    }
    if (-not $user) { return $null }

    $name = "DuckPM_$Tag"
    try { Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue } catch { }
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel $RunLevel
    $action    = New-ScheduledTaskAction -Execute $Execute -Argument $Arguments
    $settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 10)
    Register-ScheduledTask -TaskName $name -Action $action -Principal $principal -Settings $settings -TaskPath '\' -Description 'Duck Printer Manager' -Force | Out-Null
    Start-ScheduledTask -TaskName $name
    $deadline = (Get-Date).AddSeconds($WaitSec)
    Start-Sleep -Seconds 2
    while ((Get-Date) -lt $deadline) {
        $t = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
        if (-not $t -or $t.State -ne 'Running') { break }
        Start-Sleep -Seconds 1
    }
    try { Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue } catch { }
    return $user
}

function Restart-DuckSetPrt($Cfg) {
    $root = @($Cfg.SetPrtPaths | Where-Object { Test-Path -LiteralPath $_ }) | Select-Object -First 1
    if (-not $root) { L 'set_prt non presente: riavvio non necessario'; return }
    $bat = Join-Path $root ([string]$Cfg.SetPrtBatch)
    if (-not (Test-Path -LiteralPath $bat)) { L "set_prt: $bat non trovato"; return }
    if ($Cfg.SetPrtProcess) { Get-Process -Name ([string]$Cfg.SetPrtProcess) -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue }
    $u = Invoke-DuckAsLoggedOnUser 'cmd.exe' "/c `"$bat`"" 'SetPrt' 'Highest' 15
    if ($u) { L "set_prt rilanciato nella sessione di $u" } else { L 'set_prt: nessun utente collegato, verra eseguito al prossimo accesso' }
}

if ($CfgJson -eq '__LOADONLY__') { return }

# =====================================================================================
$res = @{ ok = $false; message = ''; driverMethod = ''; portMethod = ''; printerMethod = ''; defaultApplied = ''; log = $null }
try {
    $cfg = $CfgJson | ConvertFrom-Json
    $dn  = [string]$cfg.DriverName
    $pn  = [string]$cfg.PrinterName
    $pa  = [string]$cfg.PrinterAddress
    $dir = [string]$cfg.DriverDir
    $inf = $null
    if ($cfg.InfRelPath) { $inf = Join-Path $dir ([string]$cfg.InfRelPath) }

    if (-not (Get-Command Get-PrinterDriver -ErrorAction SilentlyContinue)) {
        throw 'Modulo PrintManagement non disponibile su questo sistema'
    }

    # --- Spooler
    $sp = Get-Service -Name Spooler -ErrorAction SilentlyContinue
    if (-not $sp) { throw 'Servizio Spooler non presente' }
    if ($sp.StartType -eq 'Disabled') { Set-Service -Name Spooler -StartupType Automatic; L 'Spooler era disabilitato: impostato su automatico' }
    if ($sp.Status -ne 'Running') { Start-Service -Name Spooler; Start-Sleep -Seconds 2; L 'Spooler avviato' }

    # --- 1. DRIVER (failover su 5 metodi)
    $drv = Get-PrinterDriver -Name $dn -ErrorAction SilentlyContinue
    if ($drv) {
        $res.driverMethod = 'gia presente'
        L "Driver '$dn' gia installato"
    } else {
        $infs = @(Get-ChildItem -LiteralPath $dir -Filter *.inf -Recurse -ErrorAction SilentlyContinue)
        if ($infs.Count -eq 0) { throw "Nessun file INF in $dir" }
        if (-not $inf -or -not (Test-Path -LiteralPath $inf)) { $inf = $infs[0].FullName }
        $store = Join-Path $env:SystemRoot 'System32\DriverStore\FileRepository'

        $methods = @(
            @{ N = 'pnputil + Add-PrinterDriver (DriverStore)'; S = {
                $out = & pnputil.exe /add-driver "$inf" /install 2>&1 | Out-String
                L ("pnputil: " + (($out -split "\r?\n" | Where-Object { $_ -match 'oem\d+\.inf|Published|Pubblicato|Failed|Errore|added|aggiunt' }) -join ' | '))
                $pub = $null
                if ($out -match '(oem\d+\.inf)') { $pub = $matches[1] }
                $target = $null
                if ($pub) { $target = Get-ChildItem $store -Filter $pub -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1 }
                if (-not $target) {
                    $base = [System.IO.Path]::GetFileNameWithoutExtension($inf)
                    $folder = Get-ChildItem $store -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "$base*" } |
                              Sort-Object LastWriteTime -Descending | Select-Object -First 1
                    if ($folder) { $target = Get-ChildItem $folder.FullName -Filter *.inf | Select-Object -First 1 }
                }
                Start-Sleep -Seconds 2
                if ($target) { Add-PrinterDriver -Name $dn -InfPath $target.FullName } else { Add-PrinterDriver -Name $dn }
            } },
            @{ N = 'Add-PrinterDriver -InfPath (sorgente)'; S = { Add-PrinterDriver -Name $dn -InfPath $inf } },
            @{ N = 'printui /ia'; S = {
                $p = Start-Process rundll32.exe -ArgumentList "printui.dll,PrintUIEntry /ia /m `"$dn`" /f `"$inf`" /h x64 /q" -Wait -PassThru -WindowStyle Hidden
                L "printui /ia exit $($p.ExitCode)"
            } },
            @{ N = 'pnputil /subdirs + Add-PrinterDriver'; S = {
                $out = & pnputil.exe /add-driver (Join-Path $dir '*.inf') /subdirs /install 2>&1 | Out-String
                L ("pnputil subdirs: exit $LASTEXITCODE")
                Start-Sleep -Seconds 2
                Add-PrinterDriver -Name $dn
            } },
            @{ N = 'DISM /add-driver + Add-PrinterDriver'; S = {
                $out = & dism.exe /online /add-driver "/driver:$dir" /recurse /forceunsigned 2>&1 | Out-String
                L ("dism: exit $LASTEXITCODE")
                Start-Sleep -Seconds 2
                Add-PrinterDriver -Name $dn
            } }
        )
        foreach ($m in $methods) {
            try {
                L "Driver: tentativo '$($m.N)'"
                & $m.S
            } catch { L "Driver: '$($m.N)' fallito - $($_.Exception.Message)" }
            $drv = Get-PrinterDriver -Name $dn -ErrorAction SilentlyContinue
            if ($drv) { $res.driverMethod = $m.N; L "Driver installato con '$($m.N)'"; break }
        }
        if (-not $drv) {
            $similar = (Get-PrinterDriver -ErrorAction SilentlyContinue | Where-Object { $_.Name -like "*$(($dn -split ' ')[0])*" } | Select-Object -ExpandProperty Name) -join '; '
            throw "Driver '$dn' non installabile con nessun metodo. Driver simili presenti: $similar"
        }
    }

    if ($cfg.DriverOnly) {
        $res.ok = $true
        $res.message = "Driver installato ($($res.driverMethod))"
    } else {
        # --- 2. PORTA
        $port = Get-PrinterPort -Name $pa -ErrorAction SilentlyContinue
        if ($port) { $res.portMethod = 'gia presente' }
        else {
            try { Add-PrinterPort -Name $pa -PrinterHostAddress $pa; $res.portMethod = 'Add-PrinterPort' }
            catch {
                L "Porta: Add-PrinterPort fallito - $($_.Exception.Message)"
                try {
                    $wp = ([wmiclass]'Win32_TCPIPPrinterPort').CreateInstance()
                    $wp.Name = $pa; $wp.HostAddress = $pa; $wp.PortNumber = 9100; $wp.Protocol = 1; $wp.SNMPEnabled = $false
                    [void]$wp.Put()
                    $res.portMethod = 'WMI Win32_TCPIPPrinterPort'
                } catch { L "Porta: WMI fallito - $($_.Exception.Message)" }
            }
            if (-not (Get-PrinterPort -Name $pa -ErrorAction SilentlyContinue)) { throw "Impossibile creare la porta $pa" }
            L "Porta $pa creata ($($res.portMethod))"
        }

        # --- 3. STAMPANTE (failover su 3 metodi, idempotente)
        $pr = Get-Printer -Name $pn -ErrorAction SilentlyContinue
        if ($pr) {
            if ($pr.DriverName -ne $dn -or $pr.PortName -ne $pa) {
                Set-Printer -Name $pn -DriverName $dn -PortName $pa
                $res.printerMethod = 'aggiornata (driver/porta)'
            } else { $res.printerMethod = 'gia presente' }
        } else {
            $pmethods = @(
                @{ N = 'Add-Printer'; S = { Add-Printer -Name $pn -DriverName $dn -PortName $pa } },
                @{ N = 'printui /if'; S = {
                    $p = Start-Process rundll32.exe -ArgumentList "printui.dll,PrintUIEntry /if /b `"$pn`" /r `"$pa`" /m `"$dn`" /u /q" -Wait -PassThru -WindowStyle Hidden
                    L "printui /if exit $($p.ExitCode)"
                } },
                @{ N = 'WMI Win32_Printer'; S = {
                    $wp = ([wmiclass]'Win32_Printer').CreateInstance()
                    $wp.Name = $pn; $wp.DeviceID = $pn; $wp.DriverName = $dn; $wp.PortName = $pa; $wp.Network = $false; $wp.Shared = $false
                    [void]$wp.Put()
                } }
            )
            foreach ($m in $pmethods) {
                try { L "Stampante: tentativo '$($m.N)'"; & $m.S } catch { L "Stampante: '$($m.N)' fallito - $($_.Exception.Message)" }
                if (Get-Printer -Name $pn -ErrorAction SilentlyContinue) { $res.printerMethod = $m.N; break }
            }
            if (-not $res.printerMethod) { throw "Impossibile creare la stampante '$pn'" }
        }
        L "Stampante '$pn' pronta ($($res.printerMethod))"

        # --- 4. PREDEFINITA
        $mode = [string]$cfg.DefaultMode
        $hasSetPrt = @($cfg.SetPrtPaths | Where-Object { Test-Path -LiteralPath $_ }).Count -gt 0
        $doSetPrt  = ($mode -eq 'setprt' -or $mode -eq 'both' -or ($mode -eq 'auto' -and $hasSetPrt))
        $doWindows = ($mode -eq 'windows' -or $mode -eq 'both' -or ($mode -eq 'auto' -and -not $hasSetPrt))
        $applied = @()
        if ($doSetPrt) {
            if ($hasSetPrt) {
                $files = Update-DuckSetPrt @($cfg.SetPrtPaths) $pn
                L ("set_prt: {0} file aggiornati {1}" -f $files.Count, ($files -join ', '))
                $applied += "set_prt ($($files.Count) file)"
            } else { L 'set_prt richiesto ma non presente sul PC' }
        }
        if ($doWindows) {
            $u = Invoke-DuckAsLoggedOnUser 'rundll32.exe' "printui.dll,PrintUIEntry /y /n `"$pn`"" 'Default' 'Limited' 20
            if ($u) { $applied += "Windows ($u)"; L "Predefinita di Windows impostata per $u" }
            else { L 'Predefinita di Windows: nessun utente collegato, non applicata' }
        }
        $res.defaultApplied = ($applied -join ', ')

        if ($cfg.RestartSetPrt -and $hasSetPrt) { Restart-DuckSetPrt $cfg }

        $res.ok = $true
        $res.message = "Stampante '$pn' installata (driver: $($res.driverMethod); stampante: $($res.printerMethod))"
        if ($res.defaultApplied) { $res.message += "; predefinita: $($res.defaultApplied)" }
    }
} catch {
    $res.ok = $false
    $res.message = $_.Exception.Message
    L "ERRORE: $($_.Exception.Message)"
}
# la cartella driver NON si cancella qui: se questo trasporto fallisce, HostWorker ritenta con
# un altro (WMI) sugli stessi file e poi ripulisce l'intera cartella del job
$res.log = @($Script:DuckLog)
return ($res | ConvertTo-Json -Depth 5 -Compress)
