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

    public const string DeudoresPorVentas = "1.1.06";
    public const string IvaCreditoFiscal = "1.1.07";
    public const string AnticiposGanancias = "1.1.08";
    public const string IvaDebitoFiscal = "2.1.04";
    public const string ImpuestosAPagarArf = "2.1.05";
    public const string ImpuestosAPagarArpv = "2.1.06";
    public const string TasasAPagarMunicipalidad = "2.1.07";
    public const string Ventas = "4.1.01";
    public const string ComprasEnTiendas = "5.2.02";
    public const string Monotributo = "5.3.02";
    public const string IngresosBrutos = "5.3.03";
    public const string SeguridadHigiene = "5.3.04";
    public const string Abl = "5.3.05";
    public const string Ganancias = "5.3.06";
    public const string InteresesYMultasFiscales = "5.4.02";
    public const string CuentaComitente = "1.1.03";
    public const string Acciones = "1.3.01";
    public const string Letras = "1.3.02";
    public const string CaucionesColocadas = "1.3.03";
    public const string ResultadosVentaPositivos = "4.2.01";
    public const string Dividendos = "4.2.02";
    public const string InteresesGanados = "4.2.03";
    public const string ResultadosVentaNegativos = "5.4.01";
    public const string ComisionesBursatiles = "5.4.03";
    public const string RetencionDividendos = "5.3.07";
    public const string Bonos = "1.3.04";
    public const string Garantias = "1.1.04";
    public const string PrimasCompradas = "1.3.05";
    public const string PrimasLanzadas = "2.1.08";
    public const string ResultadosDerivadosPositivos = "4.2.04";
    public const string ResultadosDerivadosNegativos = "5.4.04";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        Caja, Capital, IngresosNoClasificados, EgresosNoClasificados, DiferenciasDeRedondeo,
        DeudoresPorVentas, IvaCreditoFiscal, AnticiposGanancias, IvaDebitoFiscal, ImpuestosAPagarArf,
        ImpuestosAPagarArpv, TasasAPagarMunicipalidad, Ventas, ComprasEnTiendas, Monotributo, IngresosBrutos,
        SeguridadHigiene, Abl, Ganancias, InteresesYMultasFiscales, CuentaComitente, Acciones, Letras,
        CaucionesColocadas, ResultadosVentaPositivos, Dividendos, InteresesGanados, ResultadosVentaNegativos,
        ComisionesBursatiles, RetencionDividendos, Bonos, Garantias, PrimasCompradas, PrimasLanzadas,
        ResultadosDerivadosPositivos, ResultadosDerivadosNegativos,
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
