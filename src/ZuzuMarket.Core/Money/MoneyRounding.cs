namespace ZuzuMarket.Core.Money;

/// <summary>
/// Reglas de redondeo. Internamente los montos son <see cref="decimal"/> con 2 decimales ("centavos de oro");
/// el juego solo maneja oro entero, así que al aplicar un monto al jugador se redondea.
/// Se usa redondeo bancario (half-even) para no sesgar sistemáticamente hacia arriba.
/// </summary>
public static class MoneyRounding
{
    public const int Decimals = 2;

    /// <summary>Redondea a 2 decimales.</summary>
    public static decimal Round(decimal amount) => Math.Round(amount, Decimals, MidpointRounding.ToEven);

    /// <summary>Redondea a oro entero.</summary>
    public static long ToGold(decimal amount) => (long)Math.Round(amount, 0, MidpointRounding.ToEven);

    /// <summary>Redondea a oro entero y devuelve el residuo (monto − entero), para asentarlo como diferencia de redondeo.</summary>
    public static long ToGold(decimal amount, out decimal residue)
    {
        long gold = ToGold(amount);
        residue = amount - gold;
        return gold;
    }

    public static bool HasAtMostTwoDecimals(decimal amount) => Round(amount) == amount;
}
