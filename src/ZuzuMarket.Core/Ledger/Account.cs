namespace ZuzuMarket.Core.Ledger;

public enum AccountType
{
    Activo,
    Pasivo,
    PatrimonioNeto,
    Ingreso,
    Egreso,
}

public static class AccountTypeExtensions
{
    /// <summary>Activo y Egreso aumentan por el Debe; Pasivo, Patrimonio Neto e Ingreso, por el Haber.</summary>
    public static bool IsDebitNormal(this AccountType type) => type is AccountType.Activo or AccountType.Egreso;
}

/// <summary>Cuenta del plan de cuentas.</summary>
public sealed record Account(string Code, string Name, AccountType Type);

/// <summary>Códigos de cuentas que el mod usa directamente y deben existir en el plan de cuentas.</summary>
public static class SystemAccounts
{
    public const string Caja = "1.1.01";
    public const string Capital = "3.1.01";
    public const string IngresosNoClasificados = "4.9.01";
    public const string EgresosNoClasificados = "5.9.01";
    public const string DiferenciasDeRedondeo = "5.9.02";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        Caja, Capital, IngresosNoClasificados, EgresosNoClasificados, DiferenciasDeRedondeo,
    };
}

/// <summary>Entidades contables iniciales.</summary>
public static class LedgerEntities
{
    /// <summary>El jugador como persona humana: su oro es la cuenta Caja.</summary>
    public const string Jugador = "jugador";

    /// <summary>La granja como empresa agropecuaria.</summary>
    public const string Granja = "granja";
}
