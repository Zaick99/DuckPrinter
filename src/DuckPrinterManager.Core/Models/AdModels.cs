namespace DuckPrinterManager.Core.Models;

public sealed class AdSession
{
    public string User { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string UserDn { get; set; } = "";
    public string DomainNc { get; set; } = "";
    public string DcUsed { get; set; } = "";
    public bool CanEdit { get; set; }
    public bool CanCreate { get; set; }
    public bool IsAdmin { get; set; }
    public List<string> Reasons { get; set; } = [];
    public bool CanWrite => CanEdit || CanCreate;
}

public sealed class ProbeResult
{
    /// <summary>ok | bad_password | disabled | locked | expired | pwd_expired | must_change | restricted | skipped | error</summary>
    public string Status { get; set; } = "";
    public string Detail { get; set; } = "";

    public bool Matches => Status is "ok" or "restricted" or "must_change";
    public string Display => Status switch
    {
        "ok" => "La password coincide con il MAC: login riuscito",
        "restricted" => "La password coincide con il MAC (accesso limitato per policy)",
        "must_change" => "La password coincide, ma va cambiata al prossimo accesso",
        "bad_password" => "La password NON coincide con il MAC",
        "disabled" => "Account disabilitato",
        "locked" => "Account bloccato",
        "expired" => "Account scaduto",
        "pwd_expired" => "Password scaduta",
        "skipped" => "Prova non eseguita: " + Detail,
        "" => "Prova non eseguita",
        _ => "Errore nella prova: " + Detail,
    };
}

public sealed class AdUserInfo
{
    public string Guid { get; set; } = "";
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string SamAccountName { get; set; } = "";
    public string UserPrincipalName { get; set; } = "";
    public string DistinguishedName { get; set; } = "";
    public string ParentOu { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Enabled { get; set; }
    public bool LockedOut { get; set; }
    public bool PasswordNeverExpires { get; set; }
    public int BadPwdCount { get; set; }
    public string PasswordLastSet { get; set; } = "";
    public string LastLogonDate { get; set; } = "";
    public string WhenCreated { get; set; } = "";
    public string WhenChanged { get; set; } = "";
    public List<string> MemberOf { get; set; } = [];
    public string MacCurrent { get; set; } = "";
    public string MacSource { get; set; } = "";
    public string ArpMac { get; set; } = "";
    public string ArpIp { get; set; } = "";
    public ProbeResult? Probe { get; set; }

    public string Header => string.IsNullOrEmpty(Description) ? Name : $"{Name} - {Description}";
}

public sealed class AdSearchResult
{
    public List<AdUserInfo> Users { get; set; } = [];
    public string Note { get; set; } = "";
}

public sealed class AdUpdateResult
{
    public List<string> Changed { get; set; } = [];
    public List<string> Failed { get; set; } = [];
    public bool PasswordSet { get; set; }
    public bool DescUpdated { get; set; }
    public string NewDn { get; set; } = "";
    public ProbeResult? Probe { get; set; }
}

public sealed class AdCreateResult
{
    public bool Ok { get; set; }
    public string Reason { get; set; } = "";
    public string Name { get; set; } = "";
    public string Mac { get; set; } = "";
    public string Upn { get; set; } = "";
    public List<string> GroupsOk { get; set; } = [];
    public List<string> GroupsFailed { get; set; } = [];
    public ProbeResult? Probe { get; set; }
}

public sealed class AdGroupInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string DistinguishedName { get; set; } = "";
}
