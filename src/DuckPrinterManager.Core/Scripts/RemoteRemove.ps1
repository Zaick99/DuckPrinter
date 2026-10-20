param([string]$CfgJson)
# =====================================================================================
#  Eseguito SUL PC DI DESTINAZIONE. Rimuove tutte le stampanti collegate a una porta che
#  punta a uno degli indirizzi indicati (IP, hostname, FQDN), qualunque sia il nome della
#  porta: "10.1.1.5", "IP_10.1.1.5", "IP_10.1.1.5_1", "pr01.dominio.it", "http://pr01:631/ipp".
#  Confronto per uguaglianza (mai per sottostringa): 10.1.1.5 non tocca 10.1.1.50.
#  Compatibile con Windows PowerShell 5.1. Restituisce sempre una stringa JSON.
# =====================================================================================

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'
$Script:DuckLog = New-Object System.Collections.ArrayList
function L([string]$m) { [void]$Script:DuckLog.Add(('{0:HH:mm:ss} {1}' -f (Get-Date), $m)) }

function Get-DuckPortHost([string]$Value) {
    if (-not $Value) { return '' }
    $x = $Value.Trim().ToLowerInvariant()
    if ($x -match '^(?:https?|ipps?|lpr|socket)://\[?([^/\]:]+)') { return $matches[1] }
    $x = $x -replace '^(ip_|tcpip_|tcp_)', ''
    $x = $x -replace '_\d+$', ''
    $x = $x -replace ':\d+$', ''
    return $x.TrimEnd('.')
}

function Test-DuckPortMatch($Port, [string[]]$Targets) {
    foreach ($v in @($Port.Name, $Port.PrinterHostAddress)) {
        if (-not $v) { continue }
        $h = Get-DuckPortHost ([string]$v)
        if ($Targets -contains $h -or $Targets -contains ([string]$v).ToLowerInvariant()) { return $true }
    }
    return $false
}

if ($CfgJson -eq '__LOADONLY__') { return }

$res = @{ ok = $false; message = ''; removedPrinters = @(); removedPorts = @(); log = $null }
try {
    $cfg = $CfgJson | ConvertFrom-Json
    if (-not (Get-Command Get-Printer -ErrorAction SilentlyContinue)) { throw 'Modulo PrintManagement non disponibile su questo sistema' }

    $sp = Get-Service -Name Spooler -ErrorAction SilentlyContinue
    if ($sp -and $sp.Status -ne 'Running') { Start-Service -Name Spooler; Start-Sleep -Seconds 2 }

    # destinazioni: quelle risolte dal PC operatore + quelle risolte da qui (DNS puo' differire)
    $targets = New-Object System.Collections.ArrayList
    foreach ($t in @($cfg.Targets)) {
        $n = Get-DuckPortHost ([string]$t)
        if ($n -and -not $targets.Contains($n)) { [void]$targets.Add($n) }
        try {
            foreach ($a in [System.Net.Dns]::GetHostAddresses([string]$t)) {
                if ($a.AddressFamily -eq 'InterNetwork' -and -not $targets.Contains($a.ToString())) { [void]$targets.Add($a.ToString()) }
            }
        } catch { }
    }
    $targets = [string[]]$targets.ToArray()

    $ports = @(Get-PrinterPort -ErrorAction SilentlyContinue)
    if ($cfg.RemoveAllNetwork) {
        # come lo script originale: solo porte di rete; PDF/XPS/OneNote/USB restano dove sono
        $match = @($ports | Where-Object { $_.Description -like '*TCP/IP*' -or $_.PrinterHostAddress -or $_.Name -match '^(https?|ipps?)://' -or $_.Name -like 'WSD*' })
        L "Rimozione di tutte le stampanti di rete: $($match.Count) porte di rete"
    } else {
        if ($targets.Count -eq 0) { throw 'Nessun indirizzo stampante indicato' }
        $match = @($ports | Where-Object { Test-DuckPortMatch $_ $targets })
        L ("Indirizzi cercati: {0} -> porte trovate: {1}" -f ($targets -join ', '), (($match | ForEach-Object { $_.Name }) -join ', '))
    }
    $portNames = @($match | ForEach-Object { $_.Name })

    foreach ($p in @(Get-Printer -ErrorAction SilentlyContinue | Where-Object { $portNames -contains $_.PortName })) {
        $done = $false
        try { Remove-Printer -Name $p.Name; $done = $true }
        catch {
            L "Remove-Printer '$($p.Name)' fallito: $($_.Exception.Message) - provo printui /dl"
            $pr = Start-Process rundll32.exe -ArgumentList "printui.dll,PrintUIEntry /dl /n `"$($p.Name)`" /q" -Wait -PassThru -WindowStyle Hidden
            $done = -not (Get-Printer -Name $p.Name -ErrorAction SilentlyContinue)
        }
        if ($done) { $res.removedPrinters += $p.Name; L "Rimossa stampante '$($p.Name)' (porta $($p.PortName))" }
    }

    if ($cfg.RemovePorts) {
        Start-Sleep -Seconds 1
        $stillUsed = @(Get-Printer -ErrorAction SilentlyContinue | ForEach-Object { $_.PortName })
        foreach ($pn in $portNames) {
            if ($stillUsed -contains $pn) { L "Porta $pn ancora in uso: lasciata"; continue }
            try { Remove-PrinterPort -Name $pn; $res.removedPorts += $pn }
            catch {
                # porta "occupata" dallo spooler: riavvio e secondo tentativo
                try { Restart-Service Spooler -Force; Start-Sleep -Seconds 2; Remove-PrinterPort -Name $pn; $res.removedPorts += $pn }
                catch { L "Porta $pn non rimossa: $($_.Exception.Message)" }
            }
        }
    }

    if ($cfg.RestartSetPrt) {
        $root = @($cfg.SetPrtPaths | Where-Object { Test-Path -LiteralPath $_ }) | Select-Object -First 1
        if ($root) {
            $bat = Join-Path $root ([string]$cfg.SetPrtBatch)
            if (Test-Path -LiteralPath $bat) {
                if ($cfg.SetPrtProcess) { Get-Process -Name ([string]$cfg.SetPrtProcess) -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue }
                $cs = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
                if ($cs -and $cs.UserName) {
                    $name = 'DuckPM_SetPrt'
                    try { Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue } catch { }
                    $principal = New-ScheduledTaskPrincipal -UserId $cs.UserName -LogonType Interactive -RunLevel Highest
                    $action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument "/c `"$bat`""
                    Register-ScheduledTask -TaskName $name -Action $action -Principal $principal -TaskPath '\' -Force | Out-Null
                    Start-ScheduledTask -TaskName $name
                    Start-Sleep -Seconds 5
                    Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
                    L "set_prt rilanciato per $($cs.UserName)"
                }
            }
        }
    }

    $res.ok = $true
    if ($res.removedPrinters.Count -eq 0 -and $portNames.Count -eq 0) {
        $res.message = 'Nessuna stampante collegata agli indirizzi indicati (niente da rimuovere)'
    } else {
        $res.message = "Rimosse $($res.removedPrinters.Count) stampanti e $($res.removedPorts.Count) porte"
        if ($res.removedPrinters.Count -gt 0) { $res.message += ': ' + ($res.removedPrinters -join ', ') }
    }
} catch {
    $res.ok = $false
    $res.message = $_.Exception.Message
    L "ERRORE: $($_.Exception.Message)"
}
$res.log = @($Script:DuckLog)
return ($res | ConvertTo-Json -Depth 5 -Compress)
