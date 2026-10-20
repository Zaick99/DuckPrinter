using System.Globalization;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DuckPrinterManager.Ui;

/// <summary>
/// Avatar Duck: la papera vestita in modo diverso per ogni lettera (Assets/avatars/a..z.png,
/// generati da make_avatars.py). Stessa iniziale = stessa papera, su ogni PC.
/// </summary>
public static class DuckAvatar
{
    private static readonly Dictionary<string, ImageSource> Cache = [];

    public static ImageSource For(string name)
    {
        var key = Letter(name);
        if (Cache.TryGetValue(key, out var img)) return img;
        var bmp = new BitmapImage(new Uri($"pack://application:,,,/Assets/avatars/{key}.png"));
        bmp.Freeze();   // bitmap statica, nessun pennello del tema: congelabile
        return Cache[key] = bmp;
    }

    /// <summary>Prima lettera utile, senza accenti ("Èlia" -> e); "DOMINIO\mario" -> m.</summary>
    public static string Letter(string name)
    {
        var s = name.Contains('\\') ? name[(name.LastIndexOf('\\') + 1)..] : name;
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            var c = char.ToLowerInvariant(ch);
            if (c is >= 'a' and <= 'z') return c.ToString();
            if (char.IsLetterOrDigit(c)) break;
        }
        return "other";
    }
}
