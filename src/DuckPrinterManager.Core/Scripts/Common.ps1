# Funzioni condivise dagli script lanciati da Duck Printer Manager.
# Compatibile con Windows PowerShell 5.1 e PowerShell 7.
#   stdin : una riga base64(UTF-8 JSON) con i parametri
#   stdout: ##LOG|LIVELLO|testo  e, alla fine, ##RESULT|{json}

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

function Read-DuckInput {
    $b64 = [Console]::In.ReadLine()
    if (-not $b64) { throw 'Nessun parametro ricevuto su stdin' }
    $json = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($b64.Trim()))
    return ($json | ConvertFrom-Json)
}

function Write-DuckLog {
    param([string]$Level = 'INFO', [string]$Message)
    foreach ($line in ($Message -split "\r?\n")) {
        if ($line.Trim()) { [Console]::Out.WriteLine("##LOG|$Level|$line") }
    }
    [Console]::Out.Flush()
}

function Write-DuckResult {
    param($Object)
    [Console]::Out.WriteLine('##RESULT|' + ($Object | ConvertTo-Json -Depth 8 -Compress))
    [Console]::Out.Flush()
}

function New-DuckCredential {
    param($Cred)
    if ($Cred -and $Cred.User) {
        $sec = ConvertTo-SecureString ([string]$Cred.Password) -AsPlainText -Force
        return New-Object System.Management.Automation.PSCredential([string]$Cred.User, $sec)
    }
    return $null
}

function Get-DuckInnerError {
    # Messaggio piu' interno: i cmdlet avvolgono le COMException/Win32Exception che dicono il vero motivo
    param($ErrorRecord)
    $e = $ErrorRecord
    if ($e -is [System.Management.Automation.ErrorRecord]) { $e = $e.Exception }
    while ($e.InnerException) { $e = $e.InnerException }
    return $e
}
