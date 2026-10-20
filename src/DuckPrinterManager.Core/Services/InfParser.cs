using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace DuckPrinterManager.Core.Services;

public sealed record InfDriver(
    string DriverName,
    string Manufacturer,
    string InfPath,
    string ModelsSection,
    string Architecture,
    string Source);

public sealed class InfReadResult
{
    public string InfPath { get; init; } = "";
    public List<InfDriver> Drivers { get; } = [];
    public List<string> Warnings { get; } = [];
    public string Provider { get; set; } = "";
    public string DriverVer { get; set; } = "";
    public string Class { get; set; } = "";
    public string Method { get; set; } = "";
}

/// <summary>
/// Lettura INF a prova di errore. Primo tentativo con SetupAPI (SetupOpenInfFile & co.), cioe' lo
/// stesso motore che usa Windows per installare il driver: gestisce codifiche, righe continuate,
/// sezioni [Strings] localizzate e decorazioni NTamd64.x esattamente come pnputil/printui.
/// Se SetupAPI non c'e' (Linux/test) o rifiuta il file, si ripiega su un parser testuale tollerante.
/// </summary>
public static partial class InfParser
{
    public static InfReadResult Read(string infPath)
    {
        var full = Path.GetFullPath(infPath);
        InfReadResult? viaSetupApi = null;
        if (OperatingSystem.IsWindows())
        {
            try { viaSetupApi = SetupApiReader.Read(full); }
            catch (Exception ex) { viaSetupApi = new InfReadResult { InfPath = full }; viaSetupApi.Warnings.Add($"SetupAPI: {ex.Message}"); }
            if (viaSetupApi.Drivers.Count > 0)
            {
                viaSetupApi.Method = "SetupAPI (motore Windows)";
                return Finish(viaSetupApi);
            }
        }

        var text = TextReader.Read(full);
        text.Method = OperatingSystem.IsWindows() ? "Parser testuale (fallback)" : "Parser testuale";
        if (viaSetupApi is not null)
            text.Warnings.InsertRange(0, viaSetupApi.Warnings.Append("SetupAPI non ha restituito modelli: usato il parser testuale"));
        return Finish(text);
    }

    /// <summary>Tutti gli INF di una cartella (ricorsivo). Un INF illeggibile non ferma gli altri.</summary>
    public static List<InfReadResult> ReadFolder(string folder, CancellationToken ct = default)
    {
        var results = new List<InfReadResult>();
        foreach (var inf in Directory.EnumerateFiles(folder, "*.inf", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive }))
        {
            ct.ThrowIfCancellationRequested();
            try { results.Add(Read(inf)); }
            catch (Exception ex)
            {
                var r = new InfReadResult { InfPath = inf, Method = "errore" };
                r.Warnings.Add(ex.Message);
                results.Add(r);
            }
        }
        return results;
    }

    private static InfReadResult Finish(InfReadResult r)
    {
        if (r.Class.Length > 0 && !r.Class.Equals("Printer", StringComparison.OrdinalIgnoreCase))
            r.Warnings.Add($"Class={r.Class}: non e' un driver di stampa");
        // ordine: prima x64/neutri, poi nome
        var sorted = r.Drivers
            .GroupBy(d => d.DriverName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(d => ArchRank(d.Architecture)).First())
            .OrderBy(d => ArchRank(d.Architecture)).ThenBy(d => d.DriverName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        r.Drivers.Clear();
        r.Drivers.AddRange(sorted);
        return r;
    }

    private static int ArchRank(string arch) => arch switch { "x64" => 0, "any" => 1, "arm64" => 2, _ => 3 };

    internal static string ArchOf(string decoration)
    {
        var d = decoration.ToLowerInvariant();
        if (d.Contains("amd64")) return "x64";
        if (d.Contains("arm64")) return "arm64";
        if (d.Contains("x86")) return "x86";
        if (d.Contains("ia64")) return "ia64";
        if (d.Contains("arm")) return "arm";
        return "any";
    }

    // =====================================================================================
    private static class SetupApiReader
    {
        private const uint INF_STYLE_OLDNT = 1, INF_STYLE_WIN4 = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct INFCONTEXT { public IntPtr Inf; public IntPtr CurrentInf; public uint Section; public uint Line; }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupOpenInfFileW(string fileName, string? infClass, uint infStyle, out uint errorLine);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern void SetupCloseInfFile(IntPtr infHandle);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupFindFirstLineW(IntPtr infHandle, string section, string? key, out INFCONTEXT context);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupFindNextLine(ref INFCONTEXT contextIn, out INFCONTEXT contextOut);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern uint SetupGetFieldCount(ref INFCONTEXT context);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupGetStringFieldW(ref INFCONTEXT context, uint fieldIndex, [Out] char[]? buffer, uint bufferSize, out uint requiredSize);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupGetLineTextW(IntPtr context, IntPtr infHandle, string section, string key, [Out] char[]? buffer, uint bufferSize, out uint requiredSize);

        private static string Field(ref INFCONTEXT ctx, uint index)
        {
            if (!SetupGetStringFieldW(ref ctx, index, null, 0, out var req) || req == 0) return "";
            var buf = new char[req];
            return SetupGetStringFieldW(ref ctx, index, buf, req, out _) ? new string(buf, 0, (int)req - 1).Trim() : "";
        }

        private static string LineText(IntPtr inf, string section, string key)
        {
            if (!SetupGetLineTextW(IntPtr.Zero, inf, section, key, null, 0, out var req) || req == 0) return "";
            var buf = new char[req];
            return SetupGetLineTextW(IntPtr.Zero, inf, section, key, buf, req, out _) ? new string(buf, 0, (int)req - 1).Trim() : "";
        }

        public static InfReadResult Read(string path)
        {
            var r = new InfReadResult { InfPath = path };
            var inf = SetupOpenInfFileW(path, null, INF_STYLE_WIN4 | INF_STYLE_OLDNT, out var errLine);
            if (inf == new IntPtr(-1))
            {
                var code = Marshal.GetLastWin32Error();
                r.Warnings.Add($"SetupOpenInfFile errore {code} ({new System.ComponentModel.Win32Exception(code).Message}) alla riga {errLine}");
                return r;
            }
            try
            {
                r.Provider = LineText(inf, "Version", "Provider");
                r.DriverVer = LineText(inf, "Version", "DriverVer");
                r.Class = LineText(inf, "Version", "Class");

                var modelSections = new List<(string Section, string Manufacturer, string Arch)>();
                foreach (var mfgSection in new[] { "Manufacturer", "Manufacturers" })
                {
                    if (!SetupFindFirstLineW(inf, mfgSection, null, out var ctx)) continue;
                    do
                    {
                        var count = SetupGetFieldCount(ref ctx);
                        var mfg = Field(ref ctx, 0);
                        var baseSection = count >= 1 ? Field(ref ctx, 1) : mfg;
                        if (string.IsNullOrEmpty(baseSection)) continue;
                        if (string.IsNullOrEmpty(mfg)) mfg = baseSection;
                        modelSections.Add((baseSection, mfg, "any"));
                        for (uint i = 2; i <= count; i++)
                        {
                            var deco = Field(ref ctx, i);
                            if (deco.Length > 0) modelSections.Add(($"{baseSection}.{deco}", mfg, ArchOf(deco)));
                        }
                    } while (SetupFindNextLine(ref ctx, out ctx));
                }

                foreach (var (section, mfg, arch) in modelSections)
                {
                    if (!SetupFindFirstLineW(inf, section, null, out var ctx)) continue;
                    do
                    {
                        var name = Field(ref ctx, 0);
                        if (name.Length >= 2 && !name.StartsWith('%'))
                            r.Drivers.Add(new InfDriver(name, mfg, path, section, arch, "SetupAPI"));
                    } while (SetupFindNextLine(ref ctx, out ctx));
                }
                if (modelSections.Count == 0) r.Warnings.Add("Nessuna sezione [Manufacturer] trovata da SetupAPI");
            }
            finally { SetupCloseInfFile(inf); }
            return r;
        }
    }

    // =====================================================================================
    /// <summary>Port del Parse-InfDrivers dello script originale, con l'architettura per sezione.</summary>
    internal static partial class TextReader
    {
        [GeneratedRegex(@"\\[ \t]*\r?\n[ \t]*")] private static partial Regex Continuation();
        [GeneratedRegex(@"^\[([^\]]+)\]")] private static partial Regex Header();
        [GeneratedRegex(@"%([^%]+)%")] private static partial Regex Token();

        public static string Decode(byte[] b)
        {
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
            // UTF-16 LE senza BOM: un byte su due e' zero
            if (b.Length >= 8 && b[1] == 0 && b[3] == 0 && b[0] != 0) return Encoding.Unicode.GetString(b);
            try
            {
                return new UTF8Encoding(false, true).GetString(b);
            }
            catch (DecoderFallbackException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                int cp = 1252;
                try { cp = CultureInfo.CurrentCulture.TextInfo.ANSICodePage; } catch { }
                try { return Encoding.GetEncoding(cp).GetString(b); }
                catch { return Encoding.Latin1.GetString(b); }
            }
        }

        /// <summary>Toglie il commento ';' fuori dalle virgolette.</summary>
        private static string StripComment(string line)
        {
            bool q = false;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '"') q = !q;
                else if (line[i] == ';' && !q) return line[..i];
            }
            return line;
        }

        /// <summary>Divide "a, b ,c" sui separatori fuori dalle virgolette.</summary>
        internal static List<string> SplitFields(string s)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            bool q = false;
            foreach (var c in s)
            {
                if (c == '"') { q = !q; sb.Append(c); }
                else if (c == ',' && !q) { list.Add(sb.ToString().Trim()); sb.Clear(); }
                else sb.Append(c);
            }
            list.Add(sb.ToString().Trim());
            return list;
        }

        /// <summary>Divide chiave = valore sul primo '=' fuori dalle virgolette.</summary>
        internal static (string? Key, string Value) SplitKey(string line)
        {
            bool q = false;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '"') q = !q;
                else if (line[i] == '=' && !q) return (line[..i].Trim(), line[(i + 1)..].Trim());
            }
            return (null, line.Trim());
        }

        public static InfReadResult Read(string path)
        {
            var r = new InfReadResult { InfPath = path };
            var content = Continuation().Replace(Decode(File.ReadAllBytes(path)), " ");

            var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            List<string>? current = null;
            foreach (var rawLine in content.Split('\n'))
            {
                var line = StripComment(rawLine).Trim();
                if (line.Length == 0) continue;
                var h = Header().Match(line);
                if (h.Success)
                {
                    var name = h.Groups[1].Value.Trim();
                    if (!sections.TryGetValue(name, out current)) sections[name] = current = [];
                    continue;
                }
                current?.Add(line);
            }

            // [Strings]: prima la lingua corrente, poi 0409, poi generica, poi tutte le altre
            var strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var lcid = CultureInfo.CurrentUICulture.LCID.ToString("X4");
            var order = new[] { $"Strings.{lcid}", "Strings.0409", "Strings" }
                .Concat(sections.Keys.Where(k => k.StartsWith("Strings.", StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var ss in order)
            {
                if (!sections.TryGetValue(ss, out var lines)) continue;
                foreach (var l in lines)
                {
                    var (k, v) = SplitKey(l);
                    if (k is null) continue;
                    v = v.Length >= 2 && v[0] == '"' && v[^1] == '"' ? v[1..^1].Replace("\"\"", "\"") : v;
                    strings.TryAdd(k.Trim('"'), v);
                }
            }

            string Resolve(string text)
            {
                var res = text;
                for (int i = 0; i < 10 && Token().IsMatch(res); i++)
                    res = Token().Replace(res, m => strings.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Groups[1].Value);
                return res.Trim().Trim('"').Trim();
            }

            string Version(string key) =>
                sections.TryGetValue("Version", out var vl)
                    ? vl.Select(SplitKey).Where(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Select(p => Resolve(p.Value)).FirstOrDefault() ?? ""
                    : "";
            r.Provider = Version("Provider");
            r.DriverVer = Version("DriverVer");
            r.Class = Version("Class");

            var mfgLines = new List<string>();
            foreach (var mk in new[] { "Manufacturer", "Manufacturers" })
                if (sections.TryGetValue(mk, out var l)) mfgLines.AddRange(l);

            if (mfgLines.Count == 0)
            {
                r.Warnings.Add("Sezione [Manufacturer] assente: estrazione euristica dei modelli");
                Heuristic(sections, Resolve, r, path);
                return r;
            }

            var candidates = new List<(string Section, string Mfg, string Arch)>();
            foreach (var ml in mfgLines)
            {
                var (k, v) = SplitKey(ml);
                var fields = SplitFields(v);
                var baseSection = Resolve(fields[0]);
                if (baseSection.Length == 0) continue;
                var mfg = k is null ? baseSection : Resolve(k);
                candidates.Add((baseSection, mfg, "any"));
                foreach (var deco in fields.Skip(1).Where(f => f.Length > 0))
                    candidates.Add(($"{baseSection}.{Resolve(deco)}", mfg, ArchOf(deco)));
            }

            foreach (var (section, mfg, arch) in candidates)
            {
                if (!sections.TryGetValue(section, out var lines)) continue;
                foreach (var l in lines)
                {
                    var (k, _) = SplitKey(l);
                    if (k is null) continue;
                    var name = Resolve(k);
                    if (IsPlausibleModel(name)) r.Drivers.Add(new InfDriver(name, mfg, path, section, arch, "Testo"));
                }
            }
            if (r.Drivers.Count == 0) r.Warnings.Add("Le sezioni modello indicate in [Manufacturer] sono vuote o assenti");
            return r;
        }

        private static readonly Regex Junk = new(@"^(Install|Setup|Default|Config|Disk|Source|Dest|CopyFiles|AddReg|Models|Class|Provider|Version)|^[\d\.\-\s,]+$|^[A-Za-z]:\\", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static bool IsPlausibleModel(string s) => s.Length >= 3 && !Junk.IsMatch(s);

        private static void Heuristic(Dictionary<string, List<string>> sections, Func<string, string> resolve, InfReadResult r, string path)
        {
            var skip = new Regex(@"^(Strings|Version|SourceDisks|DestinationDirs|DefaultInstall|DefaultUninstall|CopyFiles|DelFiles|AddReg|DelReg|Services|SignatureAttributes|PkgProperties)", RegexOptions.IgnoreCase);
            foreach (var (name, lines) in sections)
            {
                if (skip.IsMatch(name)) continue;
                foreach (var l in lines)
                {
                    var (k, _) = SplitKey(l);
                    if (k is null || !(k.StartsWith('"') || k.StartsWith('%'))) continue;
                    var dn = resolve(k);
                    if (dn.Length >= 4 && IsPlausibleModel(dn)) r.Drivers.Add(new InfDriver(dn, "", path, name, ArchOf(name), "Euristica"));
                }
            }
        }
    }
}
