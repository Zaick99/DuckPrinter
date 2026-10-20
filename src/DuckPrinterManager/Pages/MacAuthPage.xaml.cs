using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DuckPrinterManager.Core.Models;
using DuckPrinterManager.Core.Native;
using DuckPrinterManager.Core.Services;
using DuckPrinterManager.Dialogs;
using DuckPrinterManager.Ui;
using Rect = System.Windows.Shapes.Rectangle;

namespace DuckPrinterManager.Pages;

/// <summary>
/// Ricerca grande; la scheda dell'utenza trovata si modifica sul posto (MAC e reparto hanno il
/// loro "Modifica"); "Nuova utenza" apre la procedura guidata in una finestra.
/// </summary>
public partial class MacAuthPage : UserControl, IPage
{
    private AdUserInfo? _user;
    private string _lastNote = "";

    public MacAuthPage()
    {
        InitializeComponent();
        AppHost.Ad.SessionChanged += UpdatePermissions;
        AppHost.ConfigChanged += () => { UpdatePermissions(); RenderInfo(); };
        AppHost.RepartiChanged += RenderInfo;
        UpdatePermissions();
        RenderInfo();
        ShowFound(false);
    }

    public void OnShown()
    {
        if (AppHost.Ad.IsLoggedIn) TxtSearch.Focus();
    }

    private Window Owner => Window.GetWindow(this);

    // ---------------------------------------------------------------- permessi
    private void UpdatePermissions()
    {
        var ad = AppHost.Ad;
        LoginGate.Visibility = ad.IsLoggedIn ? Visibility.Collapsed : Visibility.Visible;
        MainArea.Visibility = SearchRow.Visibility = ad.IsLoggedIn ? Visibility.Visible : Visibility.Collapsed;
        BtnNewUser.Visibility = ad.CanCreate ? Visibility.Visible : Visibility.Collapsed;
        if (!ad.IsLoggedIn) Dispatcher.BeginInvoke(() => Fx.Entrance(LoginGate), System.Windows.Threading.DispatcherPriority.Loaded);

        if (ad.IsLoggedIn && !ad.CanEdit && !ad.CanCreate)
        {
            LoginBanner.Visibility = Visibility.Visible;
            LoginBannerText.Text = $"Connesso come {ad.Session!.User}: SOLA LETTURA. {string.Join(" ", ad.Session.Reasons)}";
        }
        else LoginBanner.Visibility = Visibility.Collapsed;
        BtnProbe.IsEnabled = ad.IsLoggedIn;
        if (_user is not null) Show(_user, _lastNote, animate: false);
    }

    private void OnLogin(object sender, RoutedEventArgs e) => (Owner as MainWindow)?.EnsureLogin();

    // ---------------------------------------------------------------- nuova utenza (procedura guidata)
    private void OpenCreate(string? mac)
    {
        var dlg = new CreateUserDialog(Owner, mac);
        if (dlg.ShowDialog() == true && dlg.ShowMac is { } created)
        {
            TxtSearch.Text = created;
            OnSearch(BtnSearch, new RoutedEventArgs());
        }
        RenderInfo();
    }

    private void OnNewUser(object sender, RoutedEventArgs e) => OpenCreate(null);
    private void OnCreateMissing(object sender, RoutedEventArgs e) => OpenCreate(BtnCreateMissing.Tag as string);

    // ---------------------------------------------------------------- ricerca
    private void OnSearchKey(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) OnSearch(sender, e); }

    private async void OnSearch(object sender, RoutedEventArgs e)
    {
        var term = TxtSearch.Text.Trim();
        if (term.Length == 0) { Fx.Shake(TxtSearch); return; }
        if (Owner is MainWindow mw && !mw.EnsureLogin()) return;

        await Busy.Run(BtnSearch, "Ricerca…", async () =>
        {
            try
            {
                var r = await AppHost.Ad.SearchAsync(term);
                AppHost.Core.Log("INFO", $"AD: ricerca '{term}' -> {r.Users.Count} risultati");
                if (r.Users.Count == 0)
                {
                    ShowFound(false);
                    InfoTitle.Text = "Nessun risultato";
                    InfoNote.Text = string.IsNullOrEmpty(r.Note) ? $"Nessuna utenza trovata per '{term}'." : r.Note;
                    Fx.Shake(TxtSearch);
                    // MAC libero: proposta immediata di crearlo
                    var mac = DuckCore.NormalizeMac(term);
                    BtnCreateMissing.Visibility = mac is not null && AppHost.Ad.CanCreate ? Visibility.Visible : Visibility.Collapsed;
                    BtnCreateMissing.Tag = mac;
                    TxtCreateMissing.Text = $"Crea l'utenza per {mac}";
                    if (BtnCreateMissing.Visibility == Visibility.Visible) Fx.Pop(BtnCreateMissing);
                    return;
                }
                MultiPanel.Visibility = r.Users.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
                MultiText.Text = $"{r.Users.Count} utenze corrispondono a '{term}': scegline una." + (r.Note.Length > 0 ? " " + r.Note : "");
                LstResults.ItemsSource = r.Users;
                LstResults.SelectedIndex = 0;
                if (r.Users.Count == 1) Show(r.Users[0], r.Note);
            }
            catch (Exception ex)
            {
                DuckDialog.Error(Owner, "Ricerca non riuscita", ex.Message);
            }
        });
    }

    private void OnResultSelected(object sender, SelectionChangedEventArgs e)
    {
        if (LstResults.SelectedItem is AdUserInfo u) Show(u, "");
    }

    private void ShowFound(bool found)
    {
        FoundPanel.Visibility = found ? Visibility.Visible : Visibility.Collapsed;
        InfoPanel.Visibility = found ? Visibility.Collapsed : Visibility.Visible;
        if (!found)
        {
            _user = null;
            MultiPanel.Visibility = Visibility.Collapsed;
            BtnCreateMissing.Visibility = Visibility.Collapsed;
            InfoTitle.Text = "Cerca una stampante";
        }
    }

    // ---------------------------------------------------------------- scheda utenza
    private void Show(AdUserInfo u, string note, bool animate = true)
    {
        _user = u;
        _lastNote = note;
        var wasVisible = FoundPanel.Visibility == Visibility.Visible;
        ShowFound(true);
        if (animate && !wasVisible) Fx.Entrance(MainArea);
        FoundTitle.Text = u.Name;
        FoundSub.Text = string.IsNullOrEmpty(u.Description) ? "(nessun reparto in descrizione)" : u.Description;

        FoundChips.Children.Clear();
        AddChip(u.Enabled ? "Abilitato" : "Disabilitato", u.Enabled ? "ChipGreen" : "ChipRed", u.Enabled ? "Green" : "Red");
        if (u.LockedOut) AddChip("Bloccato", "ChipRed", "Red");
        if (u.PasswordNeverExpires) AddChip("Password senza scadenza", "Chip", "LabelSecondary");
        if (!AppHost.Ad.CanEdit) AddChip("Sola lettura", "Chip", "LabelTertiary");

        DetailGrid.Children.Clear();
        DetailGrid.RowDefinitions.Clear();
        AddEditableRow("MAC (e password)", u.MacCurrent.Length > 0 ? u.MacCurrent : DuckCore.NormalizeMac(u.SamAccountName) ?? "", u.MacSource.Length > 0 ? $"da {u.MacSource}" : "nessun MAC riconoscibile sull'oggetto", EditKind.Mac);
        AddEditableRow("Reparto", u.Description,
            u.Description.Length > 0 && AppHost.Reparti.Items.Count > 0 && !AppHost.Reparti.Contains(u.Description) ? "non presente nell'elenco reparti" : "", EditKind.Reparto);
        if (u.ArpMac.Length > 0) AddRow(DetailGrid, "MAC via ARP", $"{u.ArpMac}   ({u.ArpIp})" + (u.ArpMac == u.MacCurrent ? "  ✓ coincide" : "  ✗ diverso da quello in AD"), mono: true);
        AddRow(DetailGrid, "sAMAccountName", u.SamAccountName, mono: true);
        AddRow(DetailGrid, "UPN", u.UserPrincipalName, mono: true);
        AddRow(DetailGrid, "Posizione (OU)", u.ParentOu);
        AddRow(DetailGrid, "Password impostata", u.PasswordLastSet);
        AddRow(DetailGrid, "Ultimo accesso", u.LastLogonDate.Length > 0 ? u.LastLogonDate : "mai");
        AddRow(DetailGrid, "Tentativi errati", u.BadPwdCount.ToString());
        AddRow(DetailGrid, "Creato / modificato", $"{u.WhenCreated}  /  {u.WhenChanged}");
        AddRow(DetailGrid, "Gruppi", u.MemberOf.Count == 0 ? "nessuno" : string.Join("\n", u.MemberOf.OrderBy(g => g)));
        var missing = AppHost.Config.ADGroups.Where(g => !u.MemberOf.Contains(g, StringComparer.OrdinalIgnoreCase)).ToList();
        if (missing.Count > 0) AddRow(DetailGrid, "Gruppi mancanti", string.Join("\n", missing), fg: "Orange");
        AddRow(DetailGrid, "DN", u.DistinguishedName);
        if (note.Length > 0) AddRow(DetailGrid, "Nota", note, fg: "Blue");

        ShowProbe(u.Probe);
        DeleteBar.Visibility = AppHost.Ad.CanEdit ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------- eliminazione
    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_user is null) return;
        var user = _user;
        var mac = user.MacCurrent.Length > 0 ? user.MacCurrent : user.SamAccountName;
        if (!DuckDialog.Confirm(Owner, "Eliminare l'utenza?",
                $"{user.Name}  ({mac})\n{(string.IsNullOrEmpty(user.Description) ? "" : user.Description + "\n")}\n{user.DistinguishedName}\n\n" +
                "L'oggetto viene eliminato da Active Directory. Si puo' recuperare solo dal Cestino di AD, se attivo.",
                "Elimina", danger: true))
            return;

        await Busy.Run(BtnDelete, "Eliminazione…", async () =>
        {
            try
            {
                var dn = await AppHost.Ad.DeleteAsync(user);
                AppHost.Core.Log("INFO", $"AD: eliminata utenza {user.Name} ({mac}) - {dn}");
                Toast.Success("Utenza eliminata", $"{user.Name} ({mac})");
                // togli la scheda: se c'erano piu' risultati restano gli altri, altrimenti stato vuoto
                var rest = (LstResults.ItemsSource as IEnumerable<AdUserInfo>)?.Where(x => x.Guid != user.Guid).ToList() ?? [];
                if (rest.Count > 0)
                {
                    LstResults.ItemsSource = rest;
                    MultiPanel.Visibility = rest.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
                    LstResults.SelectedIndex = 0;
                    if (rest.Count == 1) Show(rest[0], "");
                }
                else
                {
                    ShowFound(false);
                    InfoTitle.Text = "Utenza eliminata";
                    InfoNote.Text = $"{user.Name} ({mac}) non e' piu' in Active Directory.";
                }
            }
            catch (Exception ex)
            {
                AppHost.Core.Log("ERR", "AD: eliminazione non riuscita - " + ex.Message);
                DuckDialog.Error(Owner, "Eliminazione non riuscita", ex.Message);
            }
        });
    }

    private void AddChip(string text, string style, string fg)
    {
        var t = new TextBlock { Text = text, Style = (Style)FindResource("TxtCaption"), FontWeight = FontWeights.SemiBold };
        t.SetResourceReference(TextBlock.ForegroundProperty, fg);
        FoundChips.Children.Add(new Border { Style = (Style)FindResource(style), Child = t, Margin = new Thickness(6, 0, 0, 0) });
    }

    private int NewRow(Grid g)
    {
        var r = g.RowDefinitions.Count;
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (r > 0)
        {
            var sep = new Rect { Height = 1, VerticalAlignment = VerticalAlignment.Top };
            sep.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "SeparatorSoft");
            Grid.SetRow(sep, r);
            Grid.SetColumnSpan(sep, Math.Max(1, g.ColumnDefinitions.Count));
            g.Children.Add(sep);
        }
        return r;
    }

    private void AddRow(Grid g, string label, string value, bool mono = false, string? fg = null)
    {
        var r = NewRow(g);
        var l = new TextBlock { Text = label, Style = (Style)FindResource("TxtCaption"), Margin = new Thickness(0, 10, 12, 10), VerticalAlignment = VerticalAlignment.Top };
        var v = new TextBox { Text = value, Style = (Style)FindResource("ReadOnlyValue"), Margin = new Thickness(0, 9, 0, 9) };
        if (mono) v.FontFamily = (FontFamily)FindResource("FMono");
        if (fg is not null) v.SetResourceReference(ForegroundProperty, fg);
        Grid.SetRow(l, r);
        Grid.SetRow(v, r);
        Grid.SetColumn(v, 1);
        if (g.ColumnDefinitions.Count > 2) Grid.SetColumnSpan(v, 2);
        g.Children.Add(l);
        g.Children.Add(v);
    }

    // ---------------------------------------------------------------- modifica sul posto
    private enum EditKind { Mac, Reparto }

    /// <summary>
    /// Riga modificabile: in lettura valore + "Modifica"; in modifica campo + Salva/Annulla, con
    /// passaggio animato fra i due stati. Invio salva, Esc annulla.
    /// </summary>
    private void AddEditableRow(string label, string value, string hint, EditKind kind)
    {
        var g = DetailGrid;
        var r = NewRow(g);
        var l = new TextBlock { Text = label, Style = (Style)FindResource("TxtCaption"), Margin = new Thickness(0, 14, 12, 10), VerticalAlignment = VerticalAlignment.Top, FontWeight = FontWeights.SemiBold };
        l.SetResourceReference(TextBlock.ForegroundProperty, "Label");

        // --- lettura
        var shown = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        var val = new TextBlock { Text = value.Length > 0 ? value : "—", FontSize = kind == EditKind.Mac ? 15 : 13.5, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        val.SetResourceReference(TextBlock.ForegroundProperty, "Label");
        if (kind == EditKind.Mac) val.FontFamily = (FontFamily)FindResource("FMono");
        shown.Children.Add(val);
        if (hint.Length > 0) shown.Children.Add(new TextBlock { Text = hint, Style = (Style)FindResource("TxtCaption"), Margin = new Thickness(0, 2, 0, 0) });

        // --- modifica
        var editor = new TextBox { Text = value, Style = (Style)FindResource(kind == EditKind.Mac ? "HintMono" : "HintField"), Height = 34, FontSize = 13.5, Tag = kind == EditKind.Mac ? "001122aabbcc" : "scrivi per i suggerimenti" };
        if (kind == EditKind.Mac) { editor.Width = 220; editor.HorizontalAlignment = HorizontalAlignment.Left; SuggestionPopup.AttachMacFilter(editor); }
        else SuggestionPopup.Attach(editor, t => AppHost.Reparti.Suggest(t));
        var editHint = new TextBlock { Style = (Style)FindResource("TxtCaption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 0, 0) };
        if (kind == EditKind.Mac)
        {
            void Hint()
            {
                var mac = DuckCore.NormalizeMac(editor.Text);
                editHint.Text = editor.Text.Length == 0 ? "" :
                    mac is null ? $"{editor.Text.Length}/12 caratteri" :
                    mac == value ? "MAC invariato" :
                    $"{(value.Length > 0 ? value : "—")} → {mac}: sostituito in tutti gli attributi (SAM, UPN, descrizione, CN…) e PASSWORD impostata a {mac}";
                editHint.SetResourceReference(TextBlock.ForegroundProperty, mac is not null && mac != value ? "Accent" : "LabelTertiary");
            }
            editor.TextChanged += (_, _) => Hint();
        }
        else editHint.Text = AppHost.Reparti.Items.Count > 0 ? "Scegli una voce dell'elenco reparti." : "";
        var editPanel = new StackPanel { Margin = new Thickness(0, 6, 0, 8), Visibility = Visibility.Collapsed, Children = { editor, editHint } };

        var cell = new Grid();
        cell.Children.Add(shown);
        cell.Children.Add(editPanel);

        // --- comandi
        var btnEdit = new Button
        {
            Style = (Style)FindResource("GhostBtn"),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = "", Style = (Style)FindResource("Icon"), FontSize = 11 }, new TextBlock { Text = "Modifica", Margin = new Thickness(5, 0, 0, 0) } } },
        };
        var btnSave = new Button { Style = (Style)FindResource("PrimaryBtn"), Content = "Salva", Height = 30, Margin = new Thickness(0, 0, 6, 0) };
        var btnCancel = new Button { Style = (Style)FindResource("QuietBtn"), Content = "Annulla", Height = 30 };
        var editCmds = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, Children = { btnSave, btnCancel } };
        var cmds = new Grid { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 8, 0, 0), Children = { btnEdit, editCmds } };
        if (!AppHost.Ad.CanEdit)
        {
            btnEdit.IsEnabled = false;
            btnEdit.ToolTip = "Le credenziali in uso non hanno permessi di modifica";
        }

        void Enter()
        {
            editor.Text = value;
            Fx.Carousel(shown, editPanel, forward: true);
            Fx.Carousel(btnEdit, editCmds, forward: true);
            Dispatcher.BeginInvoke(() => { editor.Focus(); editor.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
        }
        void Leave()
        {
            Fx.Carousel(editPanel, shown, forward: false);
            Fx.Carousel(editCmds, btnEdit, forward: false);
        }
        btnEdit.Click += (_, _) => Enter();
        btnCancel.Click += (_, _) => Leave();
        btnSave.Click += async (_, _) => await Save(kind, editor, btnSave, value);
        editor.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape) { Leave(); e.Handled = true; }
            else if (e.Key == Key.Enter && kind == EditKind.Mac) { e.Handled = true; await Save(kind, editor, btnSave, value); }
        };

        Grid.SetRow(l, r);
        Grid.SetRow(cell, r);
        Grid.SetColumn(cell, 1);
        Grid.SetRow(cmds, r);
        Grid.SetColumn(cmds, 2);
        g.Children.Add(l);
        g.Children.Add(cell);
        g.Children.Add(cmds);
    }

    private async Task Save(EditKind kind, TextBox editor, Button btn, string oldValue)
    {
        if (_user is null) return;
        var user = _user;
        string? newMac = null;
        var desc = user.Description;
        if (kind == EditKind.Mac)
        {
            newMac = DuckCore.NormalizeMac(editor.Text);
            if (newMac is null) { Fx.Shake(editor); Toast.Warning("MAC non valido", "Servono 12 caratteri esadecimali (es. 001122aabbcc)."); return; }
            if (newMac == oldValue) { Toast.Info("Nessuna modifica", "Il MAC e' invariato."); return; }
            var q = oldValue.Length > 0
                ? $"Sostituire '{oldValue}' con '{newMac}' su TUTTI gli attributi di\n{user.DistinguishedName}\n\nLa password verra' impostata a '{newMac}'."
                : $"Impostare SAM/UPN a '{newMac}' e la password a '{newMac}'?";
            if (!DuckDialog.Confirm(Owner, "Modifica del MAC", q, "Applica")) return;
        }
        else
        {
            if (!ValidateReparto(editor, out desc)) { Fx.Shake(editor); return; }
            if (desc == user.Description) { Toast.Info("Nessuna modifica", "Il reparto e' invariato."); return; }
        }

        await Busy.Run(btn, "…", async () =>
        {
            try
            {
                var r = await AppHost.Ad.UpdateAsync(user, newMac, desc);
                foreach (var c in r.Changed) AppHost.Core.Log("INFO", "AD:   OK " + c);
                foreach (var f in r.Failed) AppHost.Core.Log("WARN", "AD:   KO " + f);
                if (r.Failed.Count > 0)
                    DuckDialog.Warning(Owner, "Modifica applicata con avvisi", $"Aggiornati {r.Changed.Count} attributi.\n\nNON aggiornati:\n" + string.Join("\n", r.Failed));
                var probe = r.Probe is null ? "" : $" · prova di login: {r.Probe.Display}";
                Toast.Success(kind == EditKind.Mac ? "MAC e password aggiornati" : "Reparto aggiornato",
                    kind == EditKind.Mac ? $"{oldValue} → {newMac} su {r.Changed.Count} attributi{probe}" : $"{user.Name}: {desc}");
                // ricarica la scheda con i dati aggiornati
                TxtSearch.Text = newMac ?? user.SamAccountName;
                OnSearch(BtnSearch, new RoutedEventArgs());
            }
            catch (Exception ex)
            {
                AppHost.Core.Log("ERR", "AD: modifica non riuscita - " + ex.Message);
                DuckDialog.Error(Owner, "Modifica non riuscita", ex.Message);
            }
        });
    }

    private static bool ValidateReparto(TextBox box, out string value)
    {
        value = box.Text.Trim();
        var r = AppHost.Reparti;
        if (value.Length == 0) { Toast.Warning("Reparto", "Indicare il reparto."); return false; }
        if (r.Items.Count > 0)
        {
            if (r.Contains(value)) { value = r.Canonical(value); return true; }
            if (AppHost.Config.StrictReparti) { Toast.Warning("Reparto non in elenco", $"'{value}' non e' nell'elenco reparti: scegli una voce suggerita."); return false; }
        }
        return true;
    }

    // ---------------------------------------------------------------- prova di login
    private void ShowProbe(ProbeResult? p)
    {
        p ??= new ProbeResult { Status = "", Detail = AppHost.Config.AutoLoginProbe ? "" : "prova automatica disattivata nelle impostazioni" };
        var (style, fg, glyph) = p.Status switch
        {
            _ when p.Matches => ("ChipGreen", "Green", ""),
            "bad_password" => ("ChipRed", "Red", ""),
            "" or "skipped" => ("WellBox", "LabelSecondary", ""),
            _ => ("ChipOrange", "Orange", ""),
        };
        ProbeBox.Style = (Style)FindResource(style);
        ProbeBox.Padding = new Thickness(12, 8, 12, 8);
        ProbeGlyph.Text = glyph;
        ProbeGlyph.SetResourceReference(TextBlock.ForegroundProperty, fg);
        ProbeText.Text = "Prova di login: " + p.Display;
        ProbeText.SetResourceReference(TextBlock.ForegroundProperty, fg);
        ProbeDetail.Text = p.Detail;
    }

    private async void OnProbe(object sender, RoutedEventArgs e)
    {
        if (_user is null) return;
        await Busy.Run(BtnProbe, "Prova…", async () =>
        {
            try
            {
                var p = await AppHost.Ad.ProbeAsync(_user, _user.MacCurrent);
                _user.Probe = p;
                ShowProbe(p);
                Fx.Pop(ProbeGlyph);
                AppHost.Core.Log("INFO", $"AD: prova login {_user.SamAccountName} -> {p.Status} {p.Detail}");
            }
            catch (Exception ex) { DuckDialog.Error(Owner, "Prova login", ex.Message); }
        });
    }

    // ---------------------------------------------------------------- configurazione
    private void RenderInfo()
    {
        var c = AppHost.Config;
        InfoGrid.Children.Clear();
        InfoGrid.RowDefinitions.Clear();
        AddRow(InfoGrid, "Dominio", c.Domain);
        AddRow(InfoGrid, "Domain controller", AppHost.Ad.Session?.DcUsed is { Length: > 0 } dc ? dc : (string.IsNullOrWhiteSpace(c.DomainController) ? "Automatico" : c.DomainController));
        AddRow(InfoGrid, "OU stampanti", c.OUPath);
        AddRow(InfoGrid, "Schema nomi", $"{c.NamePattern}  (es. {Naming.NextName(c.NamePattern, [], c.IncrementalDigits)})", mono: true);
        AddRow(InfoGrid, "Gruppi alla creazione", string.Join(", ", c.ADGroups));
        AddRow(InfoGrid, "Reparti", AppHost.Reparti.Error is null
            ? $"{AppHost.Reparti.Items.Count} reparti da {c.RepartiFile}"
            : $"elenco non disponibile ({AppHost.Reparti.Error}): {c.RepartiFile}");
    }
}
