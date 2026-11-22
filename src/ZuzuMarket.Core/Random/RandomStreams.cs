using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Random;

/// <summary>
/// Crea generadores independientes por subsistema ("eventos", "commodities.trigo", ...).
/// La misma semilla de partida + fecha + nombre de stream siempre da la misma secuencia,
/// y agregar un stream nuevo no altera los existentes.
/// </summary>
public static class RandomStreams
{
    /// <summary>Stream para un día concreto (eventos diarios, precios, etc.).</summary>
    public static DeterministicRandom For(ulong gameSeed, EconomicDate date, string streamName) =>
        new(DeriveSeed(gameSeed, (ulong)date.AbsoluteDay + 1, streamName));

    /// <summary>Stream para toda la partida, independiente de la fecha (p. ej. generar empresas al inicio).</summary>
    public static DeterministicRandom ForGame(ulong gameSeed, string streamName) =>
        new(DeriveSeed(gameSeed, 0, streamName));

    internal static ulong DeriveSeed(ulong gameSeed, ulong salt, string streamName)
    {
        if (string.IsNullOrWhiteSpace(streamName))
        {
            throw new ArgumentException("El nombre del stream no puede estar vacío.", nameof(streamName));
        }

        ulong x = SplitMix64.Mix(gameSeed);
        x = SplitMix64.Mix(x ^ salt);
        x = SplitMix64.Mix(x ^ StableHash.Fnv1a64(streamName));
        return x;
    }
}
