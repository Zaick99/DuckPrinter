# =====================================================================================
#  Operazioni Active Directory per la pagina MAC AUTH (eseguito sul PC dell'operatore).
#  Richiede il modulo ActiveDirectory (RSAT). Azioni:
#    login | search | probe | names | update | delete | create | dcs | groups
#  Esito: ##RESULT|{...}  oppure  ##RESULT|{ "error": "..." }
# =====================================================================================
. (Join-Path $PSScriptRoot 'Common.ps1')
Add-Type -AssemblyName System.DirectoryServices -ErrorAction SilentlyContinue

# attributi di sistema / non scrivibili: mai toccati dalla propagazione del MAC
$Script:MacSkipAttributes = @(
    'distinguishedname','cn','name','adspath','objectguid','objectsid','objectclass','objectcategory',
    'whencreated','whenchanged','usncreated','usnchanged','memberof','member','primarygroupid',
    'useraccountcontrol','pwdlastset','lastlogon','lastlogoff','lastlogontimestamp','logoncount',
    'logonhours','badpwdcount','badpasswordtime','accountexpires','instancetype','dscorepropagationdata',
    'ntsecuritydescriptor','admincount','codepage','countrycode','samaccounttype','userpassword',
    'unicodepwd','usercertificate','thumbnailphoto','jpegphoto','iscriticalsystemobject','systemflags',
    'objectversion','msds-keyversionnumber','msds-user-account-control-computed','tokengroups'
)

function Format-DuckMac([string]$Mac) {
    if ([string]::IsNullOrWhiteSpace($Mac)) { return '' }
    $c = ($Mac -replace '[:\-\.\s]', '').ToLowerInvariant()
    if ($c -match '^[a-f0-9]{12}$') { return $c }
    return ''
}

function Get-MacMatchPattern([string]$Mac) {
    # MAC in qualunque notazione: separatore opzionale fra ogni coppia di cifre
    $parts = @()
    for ($i = 0; $i -lt 12; $i += 2) { $parts += [regex]::Escape($Mac.Substring($i, 2)) }
    return ($parts -join '[:\-\.\s]?')
}

function ConvertTo-LdapFilterValue([string]$s) {
    # per -Filter dei cmdlet AD: apici raddoppiati, niente caratteri jolly involontari
    return ($s -replace "'", "''" -replace '[\*\(\)\\]', '')
}

function Format-DuckDate($d) { if ($d) { return ([datetime]$d).ToString('yyyy-MM-dd HH:mm') } return '' }

function New-DuckDirectoryEntry([string]$DistinguishedName) {
    $path = 'LDAP://' + $DistinguishedName
    if ($Script:Server) { $path = "LDAP://$($Script:Server)/$DistinguishedName" }
    $auth = [System.DirectoryServices.AuthenticationTypes]'Secure, Sealing, Signing'
    if ($Script:Cred) {
        return New-Object System.DirectoryServices.DirectoryEntry($path, $Script:Cred.UserName, $Script:Cred.GetNetworkCredential().Password, $auth)
    }
    return New-Object System.DirectoryServices.DirectoryEntry($path)
}

# ---------------------------------------------------------------- prova automatica di login
function Test-DuckMacLogin([string]$Sam, [string]$Upn, [string]$Password) {
    # Bind LDAP sul DC scelto con utente = MAC e password = MAC. Il codice Win32 dell'errore
    # dice se la password e' sbagliata o se l'account e' bloccato/disabilitato/scaduto.
    if (-not $Password) { return @{ Status = 'skipped'; Detail = 'nessun MAC rilevato sull''oggetto' } }
    $user = $Upn
    if (-not $user) { $user = "$Sam@$($Script:Domain)" }
    $target = $Script:Server
    if (-not $target) { $target = $Script:Domain }
    $auth = [System.DirectoryServices.AuthenticationTypes]'Secure, Sealing, Signing'
    $de = New-Object System.DirectoryServices.DirectoryEntry("LDAP://$target/RootDSE", $user, $Password, $auth)
    try {
        $null = $de.psbase.NativeObject
        return @{ Status = 'ok'; Detail = "bind riuscito su $target come $user" }
    } catch {
        $e = Get-DuckInnerError $_
        $code = $e.HResult -band 0xFFFF
        $map = @{ 1326 = 'bad_password'; 1331 = 'disabled'; 1909 = 'locked'; 1793 = 'expired'; 1330 = 'pwd_expired';
                  1907 = 'must_change'; 1327 = 'restricted'; 1328 = 'restricted'; 1329 = 'restricted'; 1385 = 'restricted' }
        $st = 'error'
        if ($map.ContainsKey([int]$code)) { $st = $map[[int]$code] }
        return @{ Status = $st; Detail = ("{0} (0x{1:X})" -f $e.Message.Trim(), $e.HResult) }
    } finally { $de.Dispose() }
}

function Invoke-DuckSafeProbe($User, [string]$Password) {
    # Mai provocare un blocco account: si salta se l'utente e' gia' a un passo dalla soglia.
    if (-not $User.Enabled) { return @{ Status = 'skipped'; Detail = 'account disabilitato' } }
    if ($User.LockedOut) { return @{ Status = 'skipped'; Detail = 'account bloccato' } }
    try {
        $pol = Get-ADDefaultDomainPasswordPolicy @Script:AdParams
        $thr = [int]$pol.LockoutThreshold
        if ($thr -gt 0 -and [int]$User.badPwdCount -ge ($thr - 1)) {
            return @{ Status = 'skipped'; Detail = "badPwdCount $($User.badPwdCount) vicino alla soglia di blocco ($thr)" }
        }
    } catch { }
    return Test-DuckMacLogin $User.SamAccountName $User.UserPrincipalName $Password
}

function Get-ObjectCurrentMac($User) {
    $m = Format-DuckMac ([string]$User.SamAccountName)
    if ($m) { return @($m, 'SamAccountName') }
    foreach ($pair in @(@('UserPrincipalName', $User.UserPrincipalName), @('Name', $User.Name), @('Description', $User.Description))) {
        $v = [string]$pair[1]
        if ($v -match '([0-9a-fA-F]{2}[:\-\.]?){5}[0-9a-fA-F]{2}') {
            $m = Format-DuckMac $matches[0]
            if ($m) { return @($m, $pair[0]) }
        }
    }
    return @('', '')
}

$Script:UserProps = @('Description','UserPrincipalName','SamAccountName','Name','DisplayName','Enabled','LockedOut',
                      'PasswordLastSet','LastLogonDate','whenCreated','whenChanged','MemberOf','badPwdCount',
                      'ObjectGUID','DistinguishedName','PasswordNeverExpires')

function ConvertTo-DuckUser($u) {
    $mc = Get-ObjectCurrentMac $u
    $groups = @($u.MemberOf | ForEach-Object { ($_ -split ',')[0] -replace '^CN=', '' })
    return [ordered]@{
        Guid = [string]$u.ObjectGUID; Name = [string]$u.Name; DisplayName = [string]$u.DisplayName
        SamAccountName = [string]$u.SamAccountName; UserPrincipalName = [string]$u.UserPrincipalName
        DistinguishedName = [string]$u.DistinguishedName
        ParentOu = (([string]$u.DistinguishedName) -replace '^CN=.*?(?<!\\),', '')
        Description = [string]$u.Description; Enabled = [bool]$u.Enabled; LockedOut = [bool]$u.LockedOut
        PasswordNeverExpires = [bool]$u.PasswordNeverExpires; BadPwdCount = [int]$u.badPwdCount
        PasswordLastSet = Format-DuckDate $u.PasswordLastSet; LastLogonDate = Format-DuckDate $u.LastLogonDate
        WhenCreated = Format-DuckDate $u.whenCreated; WhenChanged = Format-DuckDate $u.whenChanged
        MemberOf = $groups; MacCurrent = $mc[0]; MacSource = $mc[1]; ArpMac = ''; ArpIp = ''; Probe = $null
    }
}

function Get-DuckUserByGuid([string]$Guid) {
    return Get-ADUser -Identity ([guid]$Guid) -Properties $Script:UserProps @Script:AdParams
}

# ---------------------------------------------------------------- propagazione del MAC (come lo script originale)
function Update-ADObjectMac([string]$DistinguishedName, [string]$OldMac, [string]$NewMac) {
    $report = @{ Changed = @(); Failed = @() }
    $rx = New-Object System.Text.RegularExpressions.Regex((Get-MacMatchPattern $OldMac), 'IgnoreCase')
    $entry = New-DuckDirectoryEntry $DistinguishedName
    try {
        $entry.RefreshCache()
        $names = @(); foreach ($n in $entry.Properties.PropertyNames) { $names += [string]$n }
        foreach ($attr in $names) {
            if ($Script:MacSkipAttributes -contains $attr.ToLowerInvariant()) { continue }
            $vals = @($entry.Properties[$attr]); $newVals = @(); $touched = $false
            foreach ($v in $vals) {
                if ($v -isnot [string] -or -not $rx.IsMatch($v)) { $newVals += $v; continue }
                $newVals += $rx.Replace($v, $NewMac); $touched = $true
            }
            if (-not $touched) { continue }
            try {
                if ($newVals.Count -eq 1) { $entry.Properties[$attr].Value = $newVals[0] } else { $entry.Properties[$attr].Value = [object[]]$newVals }
                $entry.CommitChanges()
                $report.Changed += ('{0} = {1}' -f $attr, ($newVals -join '; '))
            } catch {
                try { $entry.RefreshCache() } catch { }
                $report.Failed += ('{0} ({1})' -f $attr, (Get-DuckInnerError $_).Message)
            }
        }
        # CN per ultimo: la rinomina cambia il DN
        $cn = [string]$entry.Properties['cn'].Value
        if ($cn -and $rx.IsMatch($cn)) {
            $newCn = $rx.Replace($cn, $NewMac)
            try { $entry.Rename("CN=$newCn"); $entry.CommitChanges(); $report.Changed += "cn = $newCn" }
            catch { $report.Failed += ('cn ({0})' -f (Get-DuckInnerError $_).Message) }
        }
    } finally { try { $entry.Close() } catch { }; try { $entry.Dispose() } catch { } }
    return $report
}

# ---------------------------------------------------------------- diritti sulla OU
function Get-DuckRights([string]$UserName, [string]$Password, [string]$OuDn) {
    $out = @{ CanEdit = $false; CanCreate = $false; IsAdmin = $false; Reasons = @(); UserDn = ''; DisplayName = '' }
    $sam = ($UserName -replace '^.*\\', '') -replace '@.*$', ''
    $me = Get-ADUser -Filter "SamAccountName -eq '$(ConvertTo-LdapFilterValue $sam)'" -Properties DisplayName @Script:AdParams | Select-Object -First 1
    if (-not $me) { $out.Reasons += 'utente non trovato in AD'; return $out }
    $out.UserDn = [string]$me.DistinguishedName; $out.DisplayName = [string]$me.DisplayName

    $sids = New-Object System.Collections.Generic.HashSet[string]
    [void]$sids.Add($me.SID.Value)
    [void]$sids.Add('S-1-1-0'); [void]$sids.Add('S-1-5-11')   # Everyone, Authenticated Users
    $ue = New-DuckDirectoryEntry $me.DistinguishedName
    try {
        $ue.RefreshCache([string[]]@('tokenGroups'))
        foreach ($b in $ue.Properties['tokenGroups']) { [void]$sids.Add((New-Object System.Security.Principal.SecurityIdentifier([byte[]]$b, 0)).Value) }
    } catch { $out.Reasons += "tokenGroups non leggibile: $((Get-DuckInnerError $_).Message)" } finally { $ue.Dispose() }

    foreach ($s in $sids) {
        if ($s -match '-(512|519)$' -or $s -eq 'S-1-5-32-544' -or $s -eq 'S-1-5-32-548') { $out.IsAdmin = $true }
    }
    if ($out.IsAdmin) { $out.CanEdit = $true; $out.CanCreate = $true; $out.Reasons += 'membro di un gruppo amministrativo (Domain/Enterprise Admins, Administrators, Account Operators)'; return $out }

    $userClass = [guid]'bf967aba-0de6-11d0-a285-00aa003049e2'
    $empty = [guid]::Empty
    $oe = New-DuckDirectoryEntry $OuDn
    try {
        $oe.psbase.Options.SecurityMasks = [System.DirectoryServices.SecurityMasks]::Dacl
        $rules = $oe.psbase.ObjectSecurity.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])
        $allowEdit = $false; $allowCreate = $false; $denyEdit = $false; $denyCreate = $false
        foreach ($r in $rules) {
            if (-not $sids.Contains($r.IdentityReference.Value)) { continue }
            $rights = [string]$r.ActiveDirectoryRights
            $all = $rights -match 'GenericAll'
            $edit = $all -or $rights -match 'GenericWrite|WriteProperty'
            $create = $all -or ($rights -match 'CreateChild' -and ($r.ObjectType -eq $empty -or $r.ObjectType -eq $userClass))
            $appliesToUsers = ($r.InheritedObjectType -eq $empty -or $r.InheritedObjectType -eq $userClass)
            if ($r.AccessControlType -eq 'Allow') {
                if ($edit -and $appliesToUsers -and $r.InheritanceType -ne 'None') { $allowEdit = $true }
                if ($create) { $allowCreate = $true }
            } else {
                if ($edit -and $appliesToUsers) { $denyEdit = $true }
                if ($create) { $denyCreate = $true }
            }
        }
        $out.CanEdit = $allowEdit -and -not $denyEdit
        $out.CanCreate = $allowCreate -and -not $denyCreate
        $out.Reasons += ('ACL della OU: modifica {0}, creazione {1}' -f $(if ($out.CanEdit) { 'consentita' } else { 'non consentita' }), $(if ($out.CanCreate) { 'consentita' } else { 'non consentita' }))
    } catch {
        $out.Reasons += "ACL della OU non leggibile: $((Get-DuckInnerError $_).Message)"
    } finally { $oe.Dispose() }
    return $out
}

# =====================================================================================
try {
    $in = Read-DuckInput
    $Script:Cred   = New-DuckCredential $in.Cred
    $Script:Server = [string]$in.Server
    $Script:Domain = [string]$in.Domain
    $Script:AdParams = @{}
    if ($Script:Cred) { $Script:AdParams['Credential'] = $Script:Cred }
    if ($Script:Server) { $Script:AdParams['Server'] = $Script:Server }

    try { Import-Module ActiveDirectory -ErrorAction Stop -Verbose:$false }
    catch { throw "Modulo ActiveDirectory non disponibile. Installare RSAT: Add-WindowsCapability -Online -Name Rsat.ActiveDirectory.DS-LDS.Tools~~~~0.0.1.0" }

    switch ([string]$in.Action) {

        'login' {
            $dse = Get-ADRootDSE @Script:AdParams
            $null = Get-ADUser -Filter * -ResultSetSize 1 @Script:AdParams
            $dc = $Script:Server
            if (-not $dc) { try { $dc = (Get-ADDomainController -Discover -DomainName $Script:Domain -ErrorAction Stop).HostName | Select-Object -First 1 } catch { $dc = [string]$dse.dnsHostName } }
            $r = Get-DuckRights $in.Cred.User $in.Cred.Password ([string]$in.Ou)
            Write-DuckResult @{ DomainNc = [string]$dse.defaultNamingContext; DcUsed = [string]$dc; CanEdit = $r.CanEdit; CanCreate = $r.CanCreate
                                IsAdmin = $r.IsAdmin; Reasons = @($r.Reasons); UserDn = $r.UserDn; DisplayName = $r.DisplayName }
        }

        'search' {
            $term = ([string]$in.Term).Trim()
            if (-not $term) { throw 'Inserire un nome host o un MAC address' }
            $base = (Get-ADRootDSE @Script:AdParams).defaultNamingContext
            $mac = Format-DuckMac $term
            $note = ''
            $found = @()
            $q = @{ SearchBase = $base; Properties = $Script:UserProps; ResultSetSize = 25 } + $Script:AdParams

            if ($mac) {
                $found = @(Get-ADUser -Filter "SamAccountName -eq '$mac'" @q)
                if ($found.Count -eq 0) { $found = @(Get-ADUser -Filter "Description -like '*$mac*' -or Name -like '*$mac*' -or UserPrincipalName -like '$mac@*'" @q) }
            } else {
                $t = ConvertTo-LdapFilterValue $term
                $found = @(Get-ADUser -Filter "Name -eq '$t' -or SamAccountName -eq '$t'" @q)
                if ($found.Count -eq 0) { $found = @(Get-ADUser -Filter "Name -like '*$t*' -or DisplayName -like '*$t*' -or Description -like '*$t*'" @q) }
            }

            $arpMac = ''; $arpIp = ''
            if ($found.Count -eq 0 -and -not $mac) {
                # nome host della stampante: DNS -> ping (popola la cache ARP) -> MAC -> utente
                try {
                    $ips = @([System.Net.Dns]::GetHostAddresses($term) | Where-Object { $_.AddressFamily -eq 'InterNetwork' })
                    foreach ($ip in $ips) {
                        $ipS = $ip.ToString()
                        try { $null = (New-Object System.Net.NetworkInformation.Ping).Send($ipS, 1500) } catch { }
                        $nb = Get-NetNeighbor -IPAddress $ipS -ErrorAction SilentlyContinue | Where-Object { $_.LinkLayerAddress -and $_.LinkLayerAddress -ne '00-00-00-00-00-00' } | Select-Object -First 1
                        if ($nb) { $arpMac = Format-DuckMac $nb.LinkLayerAddress; $arpIp = $ipS; break }
                    }
                } catch { }
                if ($arpMac) {
                    Write-DuckLog 'INFO' "$term -> $arpIp -> MAC $arpMac (cache ARP)"
                    $found = @(Get-ADUser -Filter "SamAccountName -eq '$arpMac'" @q)
                    $note = "Nessun oggetto con nome '$term': trovato tramite il MAC $arpMac rilevato via ARP ($arpIp)"
                    if ($found.Count -eq 0) { $note = "Host '$term' risolto in $arpIp con MAC $arpMac, ma nessuna utenza AD con quel MAC" }
                } elseif ($found.Count -eq 0) {
                    $note = "Nessun oggetto trovato per '$term' (e MAC non rilevabile via ARP: host spento o in un'altra subnet)"
                }
            }

            $users = @()
            foreach ($u in $found) {
                $d = ConvertTo-DuckUser $u
                $d.ArpMac = $arpMac; $d.ArpIp = $arpIp
                if ($in.Probe -and $found.Count -le 5) { $d.Probe = Invoke-DuckSafeProbe $u $d.MacCurrent }
                $users += $d
            }
            Write-DuckResult @{ Users = @($users); Note = $note }
        }

        'probe' {
            $u = Get-ADUser -Filter "SamAccountName -eq '$(ConvertTo-LdapFilterValue ([string]$in.SamAccountName))'" -Properties $Script:UserProps @Script:AdParams | Select-Object -First 1
            if (-not $u) { throw 'Utente non trovato' }
            Write-DuckResult (Invoke-DuckSafeProbe $u ([string]$in.Password))
        }

        'names' {
            $prefix = ConvertTo-LdapFilterValue ([string]$in.Prefix)
            $users = @(Get-ADUser -Filter "Name -like '$prefix*'" -SearchBase ([string]$in.Ou) @Script:AdParams)
            Write-DuckResult @{ names = @($users | ForEach-Object { [string]$_.Name }) }
        }

        'update' {
            $guid = [string]$in.Guid
            $newMac = Format-DuckMac ([string]$in.NewMac)
            $oldMac = Format-DuckMac ([string]$in.OldMac)
            $res = @{ Changed = @(); Failed = @(); PasswordSet = $false; DescUpdated = $false; NewDn = ''; Probe = $null }
            $u = Get-DuckUserByGuid $guid

            if ($newMac -and $newMac -ne $oldMac) {
                $clash = Get-ADUser -Filter "SamAccountName -eq '$newMac'" @Script:AdParams | Where-Object { [string]$_.ObjectGUID -ne $guid }
                if ($clash) { throw "Il MAC $newMac e' gia' usato da $($clash.DistinguishedName)" }
                if ($oldMac) {
                    Write-DuckLog 'INFO' "Propagazione MAC $oldMac -> $newMac su $($u.DistinguishedName)"
                    $rep = Update-ADObjectMac ([string]$u.DistinguishedName) $oldMac $newMac
                    $res.Changed = @($rep.Changed); $res.Failed = @($rep.Failed)
                    # se SAM/UPN non contenevano il vecchio MAC li si allinea comunque
                    $u = Get-DuckUserByGuid $guid
                    if ($u.SamAccountName -ne $newMac -or $u.UserPrincipalName -ne "$newMac@$($Script:Domain)") {
                        Set-ADUser -Identity ([guid]$guid) -SamAccountName $newMac -UserPrincipalName "$newMac@$($Script:Domain)" @Script:AdParams
                        $res.Changed += "sAMAccountName/UPN = $newMac"
                    }
                } else {
                    Set-ADUser -Identity ([guid]$guid) -SamAccountName $newMac -UserPrincipalName "$newMac@$($Script:Domain)" @Script:AdParams
                    $res.Changed += "sAMAccountName/UPN = $newMac (nessun MAC precedente rilevato)"
                }
                Set-ADAccountPassword -Identity ([guid]$guid) -Reset -NewPassword (ConvertTo-SecureString $newMac -AsPlainText -Force) @Script:AdParams
                $res.PasswordSet = $true
            }

            $newDesc = ([string]$in.NewDesc).Trim()
            if ($newDesc -and $newDesc -ne [string]$in.OldDesc) {
                Set-ADUser -Identity ([guid]$guid) -Description $newDesc @Script:AdParams
                $res.DescUpdated = $true
            }

            $u = Get-DuckUserByGuid $guid
            $res.NewDn = [string]$u.DistinguishedName
            if ($in.Probe -and ($newMac -or (Format-DuckMac ([string]$u.SamAccountName)))) {
                $pw = $newMac; if (-not $pw) { $pw = Format-DuckMac ([string]$u.SamAccountName) }
                Start-Sleep -Seconds 2
                $res.Probe = Invoke-DuckSafeProbe $u $pw
            }
            Write-DuckResult $res
        }

        'delete' {
            # Eliminazione definitiva dell'utenza (oggetto ritrovato per GUID, non per nome).
            # Guardie: solo dentro la OU configurata e solo se l'oggetto e' ancora quello mostrato.
            $guid = [string]$in.Guid
            $u = Get-DuckUserByGuid $guid
            $dn = [string]$u.DistinguishedName
            $ou = ([string]$in.Ou).Trim()
            if ($ou -and -not $dn.EndsWith(",$ou", [StringComparison]::OrdinalIgnoreCase)) {
                throw "L'utenza $dn non e' nella OU configurata ($ou): eliminazione rifiutata"
            }
            $expected = [string]$in.ExpectedSam
            if ($expected -and [string]$u.SamAccountName -ne $expected) {
                throw "L'utenza e' cambiata nel frattempo (sAMAccountName ora '$($u.SamAccountName)', atteso '$expected'): ripeti la ricerca"
            }
            # 1) cancellazione normale: basta "Elimina" sull'utenza o "Elimina oggetti Utente" sulla OU.
            #    (-Recursive usa il controllo LDAP Tree Delete, che richiede il diritto "Elimina
            #    sottoalbero", quasi mai delegato: era la causa di "insufficient access rights".)
            # 2) solo se l'oggetto ha figli (non foglia): figli uno a uno dal piu' profondo, poi l'utenza.
            # 3) ultimo tentativo: Tree Delete.
            $step = 'eliminazione'
            try {
                Remove-ADObject -Identity ([guid]$guid) -Confirm:$false @Script:AdParams
            } catch {
                $m1 = (Get-DuckInnerError $_).Message
                if ($m1 -notmatch 'non.?leaf|non foglia|8213|0x2015|subtree|sottoalbero|child') {
                    $prot = $false
                    try { $prot = [bool](Get-ADObject -Identity ([guid]$guid) -Properties ProtectedFromAccidentalDeletion @Script:AdParams).ProtectedFromAccidentalDeletion } catch { }
                    if ($prot) { throw "L'oggetto e' protetto dall'eliminazione accidentale: togliere la protezione (scheda Oggetto in Utenti e computer di AD). ($m1)" }
                    if ($m1 -match 'insufficient|Accesso negato|Access is denied|diritti') {
                        throw "Permessi insufficienti per eliminare $dn con $($in.Cred.User): serve 'Elimina' sull'utenza o 'Elimina oggetti Utente' sulla OU. ($m1)"
                    }
                    throw
                }
                Write-DuckLog 'INFO' "L'utenza contiene oggetti figli: li elimino uno per uno"
                $step = 'eliminazione degli oggetti figli'
                try {
                    $children = @(Get-ADObject -SearchBase $dn -SearchScope Subtree -Filter * @Script:AdParams |
                        Where-Object { [string]$_.ObjectGUID -ne $guid } |
                        Sort-Object { ([string]$_.DistinguishedName).Split(',').Count } -Descending)
                    foreach ($c in $children) {
                        Write-DuckLog 'DEBUG' "  elimino $($c.DistinguishedName)"
                        Remove-ADObject -Identity $c.ObjectGUID -Confirm:$false @Script:AdParams
                    }
                    Remove-ADObject -Identity ([guid]$guid) -Confirm:$false @Script:AdParams
                } catch {
                    $m2 = (Get-DuckInnerError $_).Message
                    Write-DuckLog 'WARN' "Eliminazione dei figli non riuscita ($m2): provo Tree Delete"
                    $step = 'Tree Delete'
                    try { Remove-ADObject -Identity ([guid]$guid) -Recursive -Confirm:$false @Script:AdParams }
                    catch {
                        $m3 = (Get-DuckInnerError $_).Message
                        throw "L'utenza ha oggetti figli e non e' stato possibile eliminarli: $m2. Tree Delete: $m3 (richiede il diritto 'Elimina sottoalbero')"
                    }
                }
            }
            Write-DuckLog 'INFO' "Eliminata utenza $dn"
            Write-DuckResult @{ Deleted = $true; DistinguishedName = $dn; Name = [string]$u.Name }
        }

        'create' {
            $mac = Format-DuckMac ([string]$in.Mac)
            if (-not $mac) { throw 'MAC non valido' }
            $name = [string]$in.Name; $ou = [string]$in.Ou
            if (Get-ADUser -Filter "Name -eq '$(ConvertTo-LdapFilterValue $name)'" -SearchBase $ou @Script:AdParams) { Write-DuckResult @{ Ok = $false; Reason = 'NameExists'; Name = $name }; break }
            $ex = Get-ADUser -Filter "SamAccountName -eq '$mac'" @Script:AdParams
            if ($ex) { Write-DuckResult @{ Ok = $false; Reason = 'MacExists'; Name = [string]$ex.Name; Mac = $mac }; break }

            $upn = "$mac@$($Script:Domain)"
            $p = @{
                Name = $name; DisplayName = $name; GivenName = $name; SamAccountName = $mac; UserPrincipalName = $upn
                Description = [string]$in.Desc; Path = $ou; Enabled = $true; PasswordNeverExpires = $true; CannotChangePassword = $true
                AccountPassword = (ConvertTo-SecureString ([string]$in.TempPassword) -AsPlainText -Force)
            } + $Script:AdParams
            New-ADUser @p
            Write-DuckLog 'INFO' "Creato $name ($mac) in $ou"

            $ok = @(); $ko = @()
            foreach ($g in @($in.Groups)) {
                if (-not $g) { continue }
                try { Add-ADGroupMember -Identity ([string]$g) -Members $mac @Script:AdParams; $ok += [string]$g }
                catch { $ko += ('{0}: {1}' -f $g, (Get-DuckInnerError $_).Message) }
            }
            Set-ADAccountPassword -Identity $mac -Reset -NewPassword (ConvertTo-SecureString $mac -AsPlainText -Force) @Script:AdParams

            $probe = $null
            if ($in.Probe) {
                Start-Sleep -Seconds 2
                $u = Get-ADUser -Identity $mac -Properties $Script:UserProps @Script:AdParams
                $probe = Invoke-DuckSafeProbe $u $mac
            }
            Write-DuckResult @{ Ok = $true; Name = $name; Mac = $mac; Upn = $upn; GroupsOk = $ok; GroupsFailed = $ko; Probe = $probe }
        }

        'dcs' {
            $p = @{}; if ($Script:Cred) { $p['Credential'] = $Script:Cred }
            $dcs = @()
            try { $dcs = @(Get-ADDomainController -Filter * -Server $Script:Domain @p | Select-Object -ExpandProperty HostName | Sort-Object) }
            catch { try { $dcs = @((Get-ADDomainController -Discover -DomainName $Script:Domain).HostName) } catch { } }
            Write-DuckResult @{ dcs = @($dcs) }
        }

        'groups' {
            $t = ConvertTo-LdapFilterValue ([string]$in.Term)
            $g = @(Get-ADGroup -Filter "Name -like '*$t*'" -Properties Description -ResultSetSize 50 @Script:AdParams | Sort-Object Name)
            Write-DuckResult @{ groups = @($g | ForEach-Object { @{ Name = [string]$_.Name; Description = [string]$_.Description; DistinguishedName = [string]$_.DistinguishedName } }) }
        }

        default { throw "Azione AD sconosciuta: $($in.Action)" }
    }
} catch {
    $e = Get-DuckInnerError $_
    $msg = $e.Message
    if ($msg -match 'password|credential|logon|authentication|1326|52e') { $msg = "Credenziali non valide o non autorizzate: $msg" }
    Write-DuckResult @{ error = $msg }
}
