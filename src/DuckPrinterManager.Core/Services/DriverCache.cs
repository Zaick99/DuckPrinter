using System.Security.Cryptography;
using System.Text;

namespace DuckPrinterManager.Core.Services;

/// <summary>
/// Copia locale dei file driver: un task in coda da giorni non deve dipendere da una share o da
/// una chiavetta che nel frattempo non c'e' piu'. Stesso contenuto = stessa cartella (hash).
/// </summary>
public static class DriverCache
{
    private const string Marker = ".duck_complete";

    public static string Fingerprint(string sourceDir)
    {
        using var sha = SHA256.Create();
        var sb = new StringBuilder(Path.GetFullPath(sourceDir).ToLowerInvariant());
        foreach (var f in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var fi = new FileInfo(f);
            sb.Append('|').Append(Path.GetRelativePath(sourceDir, f).ToLowerInvariant()).Append(':').Append(fi.Length).Append(':').Append(fi.LastWriteTimeUtc.Ticks);
        }
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())))[..16].ToLowerInvariant();
    }

    public static string Ensure(string sourceDir, string cacheRoot, CancellationToken ct = default)
    {
        if (!Directory.Exists(sourceDir)) throw new DirectoryNotFoundException("Cartella driver non trovata: " + sourceDir);
        if (Path.GetFullPath(sourceDir).StartsWith(Path.GetFullPath(cacheRoot), StringComparison.OrdinalIgnoreCase)) return sourceDir;

        var target = Path.Combine(cacheRoot, Fingerprint(sourceDir));
        if (File.Exists(Path.Combine(target, Marker))) return target;

        var tmp = target + ".tmp-" + Environment.ProcessId;
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        foreach (var f in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var dest = Path.Combine(tmp, Path.GetRelativePath(sourceDir, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest, true);
        }
        File.WriteAllText(Path.Combine(tmp, Marker), sourceDir);
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.Move(tmp, target);
        return target;
    }

    /// <summary>Elimina dalla cache le cartelle non piu' usate da nessun task.</summary>
    public static int Prune(string cacheRoot, IEnumerable<string> inUse)
    {
        if (!Directory.Exists(cacheRoot)) return 0;
        var keep = new HashSet<string>(inUse.Where(p => !string.IsNullOrEmpty(p)).Select(p => Path.GetFullPath(p).TrimEnd('\\', '/')), StringComparer.OrdinalIgnoreCase);
        int n = 0;
        foreach (var d in Directory.EnumerateDirectories(cacheRoot))
        {
            if (keep.Contains(Path.GetFullPath(d).TrimEnd('\\', '/'))) continue;
            try { Directory.Delete(d, true); n++; } catch { }
        }
        return n;
    }
}
