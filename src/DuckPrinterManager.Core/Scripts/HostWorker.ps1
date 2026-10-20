# =====================================================================================
#  Orchestratore di UN task su UN host (eseguito sul PC dell'operatore).
#  Account in cascata (Creds: null = utente Windows corrente, poi/prima le credenziali AD):
#  si passa all'account successivo se nessun trasporto ha potuto eseguire il payload e almeno
#  uno ha ricevuto "accesso negato" (la rete funziona, mancano i permessi).
#  Trasporti in cascata, per ogni account:
#    file driver : SMB (\\host\C$)  ->  WinRM Copy-Item -ToSession
#    esecuzione  : WinRM Invoke-Command  ->  WMI/DCOM Win32_Process.Create (+ file su SMB)
#  Il payload (RemoteDeploy/RemoteRemove) a sua volta prova piu' metodi per driver, porta,
#  stampante. Esito: ##RESULT|{ status: ok|offline|error, message, transport }
# =====================================================================================
. (Join-Path $PSScriptRoot 'Common.ps1')

$result = @{ status = 'error'; message = ''; transport = '' }

# "Accesso negato" in tutte le lingue/forme con cui arriva da WinRM, SMB e DCOM
$DeniedPattern = 'Accesso negato|Access is denied|Access denied|0x80070005|E_ACCESSDENIED|Errore di accesso|Logon failure|nome utente o la password|user name or password|unknown user name|non e'' stato possibile autenticare|account.*(disabilitato|disabled|bloccato|locked)'

function Test-Denied([string]$m) { return ($m -match $DeniedPattern) }

try {
    $in   = Read-DuckInput
    $h    = [string]$in.Host
    $timeoutSec = [int]$in.TimeoutSec
    if ($timeoutSec -lt 60) { $timeoutSec = 900 }

    $root    = ([string]$in.RemoteDir).TrimEnd('\')
    if ($root -notmatch '^([A-Za-z]):(\\.*)?$') { throw "RemoteDir deve essere un percorso locale (es. C:\ProgramData\DuckPM): $root" }
    $driveLetter = $matches[1]; $rest = $matches[2]

    $isDeploy = ($in.Action -eq 'deploy')
    $cfg = $null
    if ($isDeploy) { $cfg = $in.Deploy } else { $cfg = $in.Remove }

    # compatibilita': vecchio formato con un solo Cred
    $credList = @()
    if ($in.PSObject.Properties['Creds']) { $credList = @($in.Creds) }
    elseif ($in.PSObject.Properties['Cred']) { $credList = @($in.Cred) }
    if ($credList.Count -eq 0) { $credList = @($null) }

    $me = "$([Environment]::UserDomainName)\$([Environment]::UserName)"

    function Read-PayloadResult($raw) {
        $text = @($raw | Where-Object { $_ -is [string] -and $_.TrimStart().StartsWith('{') }) | Select-Object -Last 1
        if (-not $text) { throw "il payload non ha restituito un risultato: $(@($raw) -join ' ')" }
        $r = $text | ConvertFrom-Json
        foreach ($l in @($r.log)) { Write-DuckLog 'DEBUG' "  [$h] $l" }
        return $r
    }

    # ================================================================ un tentativo completo con UN account
    function Invoke-Attempt($credIn) {
        $cred    = New-DuckCredential $credIn
        $who     = if ($cred) { $cred.UserName } else { "$me (Windows)" }
        $jobId   = ('{0}_{1}' -f $in.TaskId, ([guid]::NewGuid().ToString('N').Substring(0, 6)))
        $jobDir  = "$root\job_$jobId"                       # percorso visto dal PC remoto
        $att = @{ ok = $false; final = $null; transport = ''; errors = @(); ran = $false; denied = $false; who = $who }
        $drive = $null; $session = $null; $smbRoot = $null; $smbError = ''

        Write-DuckLog 'INFO' "Account: $who"
        try {
            # ------------------------------------------------------------ SMB (share amministrativa)
            try {
                $unc = "\\$h\$driveLetter`$"
                if ($cred) {
                    $dn = 'DK' + ([guid]::NewGuid().ToString('N').Substring(0, 6))
                    $drive = New-PSDrive -Name $dn -PSProvider FileSystem -Root $unc -Credential $cred -ErrorAction Stop
                    $smbRoot = "$($dn):$rest"
                } else {
                    if (-not (Test-Path -LiteralPath $unc)) { throw "share $unc non accessibile (accesso negato o share amministrative disattivate)" }
                    $smbRoot = "$unc$rest"
                }
                New-Item -ItemType Directory -Path "$smbRoot\job_$jobId" -Force | Out-Null
                Write-DuckLog 'DEBUG' "SMB: $unc accessibile"
            } catch {
                $smbRoot = $null
                $smbError = (Get-DuckInnerError $_).Message
                if (Test-Denied $smbError) { $att.denied = $true }
                Write-DuckLog 'WARN' "SMB non disponibile ($smbError): provo solo WinRM"
            }

            function New-RemoteConfigJson {
                $o = @{}
                foreach ($p in $cfg.PSObject.Properties) { $o[$p.Name] = $p.Value }
                if ($isDeploy) { $o['DriverDir'] = "$jobDir\drv" }
                $o['TaskId'] = [string]$in.TaskId
                return ($o | ConvertTo-Json -Depth 6 -Compress)
            }

            if ($isDeploy -and $smbRoot) {
                try {
                    $dst = "$smbRoot\job_$jobId\drv"
                    New-Item -ItemType Directory -Path $dst -Force | Out-Null
                    Copy-Item -Path (Join-Path ([string]$cfg.LocalDriverDir) '*') -Destination $dst -Recurse -Force
                    Write-DuckLog 'INFO' 'File driver copiati via SMB'
                } catch { Write-DuckLog 'WARN' "Copia SMB fallita: $($_.Exception.Message)"; $smbError = $_.Exception.Message; $smbRoot = $null }
            }

            # ------------------------------------------------------------ trasporto 1: WinRM
            function Invoke-ViaWinRM {
                $so = New-PSSessionOption -OpenTimeout 20000 -OperationTimeout ($timeoutSec * 1000) -NoMachineProfile
                $p = @{ ComputerName = $h; SessionOption = $so; ErrorAction = 'Stop' }
                if ($cred) { $p['Credential'] = $cred }
                $script:session = New-PSSession @p
                if ($isDeploy) {
                    Invoke-Command -Session $script:session -ScriptBlock { param($d) New-Item -ItemType Directory -Path $d -Force | Out-Null } -ArgumentList "$jobDir\drv"
                    if (-not $smbRoot) {
                        Write-DuckLog 'INFO' 'Copia driver via WinRM (Copy-Item -ToSession)'
                        Copy-Item -Path (Join-Path ([string]$cfg.LocalDriverDir) '*') -Destination "$jobDir\drv" -ToSession $script:session -Recurse -Force
                    }
                }
                $raw = Invoke-Command -Session $script:session -ScriptBlock ([scriptblock]::Create([string]$in.PayloadText)) -ArgumentList (New-RemoteConfigJson)
                return Read-PayloadResult $raw
            }

            # ------------------------------------------------------------ trasporto 2: WMI/DCOM
            function Invoke-ViaWmi {
                if (-not $smbRoot) { throw "WMI richiede la share amministrativa per scambiare i file, che non e' disponibile: $smbError" }
                $enc = New-Object System.Text.UTF8Encoding($true)
                $smbJob = "$smbRoot\job_$jobId"
                [System.IO.File]::WriteAllText((Convert-Path -LiteralPath $smbJob) + '\payload.ps1', [string]$in.PayloadText, $enc)
                [System.IO.File]::WriteAllText((Convert-Path -LiteralPath $smbJob) + '\cfg.json', (New-RemoteConfigJson), $enc)
                $runner = @"
`$ErrorActionPreference = 'Stop'
try {
    `$sb  = [scriptblock]::Create([System.IO.File]::ReadAllText('$jobDir\payload.ps1'))
    `$out = & `$sb ([System.IO.File]::ReadAllText('$jobDir\cfg.json'))
    `$txt = @(`$out | Where-Object { `$_ -is [string] }) | Select-Object -Last 1
} catch { `$txt = (@{ ok = `$false; message = `$_.Exception.Message; log = @() } | ConvertTo-Json -Compress) }
[System.IO.File]::WriteAllText('$jobDir\result.tmp', [string]`$txt, (New-Object System.Text.UTF8Encoding(`$false)))
Move-Item '$jobDir\result.tmp' '$jobDir\result.json' -Force
"@
                [System.IO.File]::WriteAllText((Convert-Path -LiteralPath $smbJob) + '\run.ps1', $runner, $enc)

                $so = New-CimSessionOption -Protocol Dcom
                $p = @{ ComputerName = $h; SessionOption = $so; ErrorAction = 'Stop' }
                if ($cred) { $p['Credential'] = $cred }
                $cs = New-CimSession @p
                try {
                    $cmd = "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$jobDir\run.ps1`""
                    $r = Invoke-CimMethod -CimSession $cs -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = $cmd }
                    if ($r.ReturnValue -ne 0) { throw "Win32_Process.Create ha restituito $($r.ReturnValue)" }
                    Write-DuckLog 'DEBUG' "WMI: processo remoto avviato (PID $($r.ProcessId)), attendo l'esito"
                } finally { Remove-CimSession $cs -ErrorAction SilentlyContinue }

                $resFile = "$smbJob\result.json"
                $deadline = (Get-Date).AddSeconds($timeoutSec)
                while ((Get-Date) -lt $deadline) {
                    if (Test-Path -LiteralPath $resFile) { return Read-PayloadResult (Get-Content -LiteralPath $resFile -Raw) }
                    Start-Sleep -Seconds 3
                }
                throw "WMI: nessun esito entro $timeoutSec secondi"
            }

            # ------------------------------------------------------------ cascata dei trasporti
            foreach ($t in @('WinRM', 'WMI/DCOM')) {
                try {
                    Write-DuckLog 'INFO' "Esecuzione remota via $t"
                    if ($t -eq 'WinRM') { $r = Invoke-ViaWinRM } else { $r = Invoke-ViaWmi }
                    $att.final = $r
                    $att.transport = $t
                    $att.ran = $true              # il payload e' partito: l'account va bene
                    if ($r.ok) { $att.ok = $true; break }
                    $att.errors += "${t}: $($r.message)"
                    Write-DuckLog 'WARN' "$t ha eseguito il payload ma l'operazione non e' riuscita: $($r.message)"
                    break                          # stesso payload, stesso esito con un altro trasporto
                } catch {
                    $msg = (Get-DuckInnerError $_).Message
                    $att.errors += "${t}: $msg"
                    Write-DuckLog 'WARN' "$t non disponibile: $msg"
                    if (Test-Denied $msg) { $att.denied = $true }
                }
            }
        } finally {
            try {
                if ($smbRoot) { Remove-Item -LiteralPath "$smbRoot\job_$jobId" -Recurse -Force -ErrorAction SilentlyContinue }
                elseif ($script:session) { Invoke-Command -Session $script:session -ScriptBlock { param($d) Remove-Item -LiteralPath $d -Recurse -Force -ErrorAction SilentlyContinue } -ArgumentList $jobDir }
            } catch { }
            if ($script:session) { Remove-PSSession $script:session -ErrorAction SilentlyContinue; $script:session = $null }
            if ($drive) { Remove-PSDrive -Name $drive.Name -Force -ErrorAction SilentlyContinue }
        }
        return $att
    }

    # ================================================================ cascata degli account
    $attempts = @()
    foreach ($c in $credList) {
        $a = Invoke-Attempt $c
        $attempts += $a
        if ($a.ok -or $a.ran -or -not $a.denied) { break }
        Write-DuckLog 'WARN' "Accesso negato a $h per $($a.who)"
    }
    $last = $attempts[-1]

    if ($last.ok) {
        $result.status = 'ok'
        $result.transport = $last.transport
        $result.message = "$($last.final.message) [via $($last.transport) come $($last.who)]"
    } elseif (-not $last.ran -and $last.denied) {
        # solo "accesso negato": il problema sono i permessi, non la rete
        $whoList = ($attempts | ForEach-Object { $_.who }) -join ', '
        $hint = if ($in.LoggedIn) {
            "l'account deve essere amministratore locale del PC (gruppo Administrators, anche tramite un gruppo di dominio)."
        } else {
            "l'account deve essere amministratore locale del PC. Accedi con un account amministrativo (riquadro Accedi in alto a sinistra): il task lo usera' al prossimo tentativo."
        }
        $result.status = 'error'
        $result.message = "Accesso negato su $h con $whoList - $hint Dettagli: " + ($last.errors -join ' | ')
    } else {
        $result.status = 'error'
        $result.message = "[come $($last.who)] " + ($last.errors -join ' | ')
    }
} catch {
    $result.status = 'error'
    $result.message = (Get-DuckInnerError $_).Message
}
Write-DuckResult $result
