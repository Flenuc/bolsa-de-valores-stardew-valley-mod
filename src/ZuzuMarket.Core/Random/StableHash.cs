using System.Text;

namespace ZuzuMarket.Core.Random;

/// <summary>
/// Hash estable entre ejecuciones y plataformas (FNV-1a de 64 bits sobre UTF-8).
/// No usar <see cref="string.GetHashCode()"/>: en .NET cambia en cada ejecución.
/// </summary>
public static class StableHash
{
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static ulong Fnv1a64(string text)
    {
        ulong hash = OffsetBasis;
        foreach (byte b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash = unchecked(hash * Prime);
        }

        return hash;
    }
}
