namespace ZuzuMarket.Core.Random;

/// <summary>
/// Generador pseudoaleatorio determinista xoshiro256** (Blackman &amp; Vigna), sembrado con SplitMix64.
/// Da la misma secuencia en cualquier máquina y versión de .NET, a diferencia de <see cref="System.Random"/>.
/// </summary>
public sealed class DeterministicRandom
{
    private ulong _s0;
    private ulong _s1;
    private ulong _s2;
    private ulong _s3;
    private double? _spareGaussian;

    public DeterministicRandom(ulong seed)
    {
        ulong x = seed;
        _s0 = SplitMix64.Next(ref x);
        _s1 = SplitMix64.Next(ref x);
        _s2 = SplitMix64.Next(ref x);
        _s3 = SplitMix64.Next(ref x);
    }

    /// <summary>Construye el generador con un estado interno explícito (para tests con vectores de referencia).</summary>
    internal DeterministicRandom(ulong s0, ulong s1, ulong s2, ulong s3)
    {
        if ((s0 | s1 | s2 | s3) == 0)
        {
            throw new ArgumentException("El estado de xoshiro256** no puede ser todo ceros.");
        }

        _s0 = s0;
        _s1 = s1;
        _s2 = s2;
        _s3 = s3;
    }

    public ulong NextUInt64()
    {
        unchecked
        {
            ulong result = RotateLeft(_s1 * 5, 7) * 9;
            ulong t = _s1 << 17;

            _s2 ^= _s0;
            _s3 ^= _s1;
            _s1 ^= _s2;
            _s0 ^= _s3;
            _s2 ^= t;
            _s3 = RotateLeft(_s3, 45);

            return result;
        }
    }

    /// <summary>Número en [0, 1) con 53 bits de precisión.</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Entero en [minInclusive, maxExclusive), sin sesgo de módulo.</summary>
    public int NextInt(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), "maxExclusive debe ser mayor que minInclusive.");
        }

        ulong range = (ulong)((long)maxExclusive - minInclusive);
        ulong limit = ulong.MaxValue - (ulong.MaxValue % range);
        ulong value;
        do
        {
            value = NextUInt64();
        }
        while (value >= limit);

        return (int)(minInclusive + (long)(value % range));
    }

    /// <summary>Devuelve <c>true</c> con la probabilidad indicada (0 a 1).</summary>
    public bool NextBool(double probability = 0.5) => NextDouble() < probability;

    /// <summary>Normal estándar N(0, 1) por Box-Muller.</summary>
    public double NextGaussian()
    {
        if (_spareGaussian is double spare)
        {
            _spareGaussian = null;
            return spare;
        }

        double u1;
        do
        {
            u1 = NextDouble();
        }
        while (u1 <= double.Epsilon);

        double u2 = NextDouble();
        double radius = Math.Sqrt(-2.0 * Math.Log(u1));
        double angle = 2.0 * Math.PI * u2;
        _spareGaussian = radius * Math.Sin(angle);
        return radius * Math.Cos(angle);
    }

    public double NextGaussian(double mean, double standardDeviation) => mean + (standardDeviation * NextGaussian());

    private static ulong RotateLeft(ulong value, int bits) => (value << bits) | (value >> (64 - bits));
}

/// <summary>SplitMix64: se usa para expandir semillas y mezclar valores.</summary>
public static class SplitMix64
{
    public static ulong Next(ref ulong state)
    {
        unchecked
        {
            state += 0x9E3779B97F4A7C15UL;
            ulong z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    public static ulong Mix(ulong value)
    {
        ulong state = value;
        return Next(ref state);
    }
}
