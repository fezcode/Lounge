namespace Lounge.Core.Media;

/// <summary>tivi's natcmp: case-insensitive, digit runs compare as numbers, so
/// "E2" sorts before "E10" and leading zeros do not matter.</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        int a = 0, b = 0;
        while (a < x.Length && b < y.Length)
        {
            if (char.IsAsciiDigit(x[a]) && char.IsAsciiDigit(y[b]))
            {
                int pa = a; while (pa < x.Length && x[pa] == '0') pa++;
                int pb = b; while (pb < y.Length && y[pb] == '0') pb++;
                int ea = pa; while (ea < x.Length && char.IsAsciiDigit(x[ea])) ea++;
                int eb = pb; while (eb < y.Length && char.IsAsciiDigit(y[eb])) eb++;
                if (ea - pa != eb - pb) return ea - pa < eb - pb ? -1 : 1;
                for (; pa < ea; pa++, pb++)
                    if (x[pa] != y[pb]) return x[pa] < y[pb] ? -1 : 1;
                a = ea; b = eb;
            }
            else
            {
                int ca = char.ToLowerInvariant(x[a]), cb = char.ToLowerInvariant(y[b]);
                if (ca != cb) return ca - cb;
                a++; b++;
            }
        }
        return (a < x.Length ? 1 : 0) - (b < y.Length ? 1 : 0);
    }
}
