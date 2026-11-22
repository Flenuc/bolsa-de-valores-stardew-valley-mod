namespace ZuzuMarket.Core.Trade;

/// <summary>Comercio exterior (§6.3 y §9 del diseño): retenciones, aranceles y exportación a Gotoro.</summary>
public sealed class TradeRules
{
    /// <summary>Derechos de exportación por commodity (0,33 = 33%), × el multiplicador de la dificultad.</summary>
    public Dictionary<string, decimal> ExportDuties { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Parte de la retención que el acopiador traslada al precio del cajón (precio FAS).</summary>
    public decimal FasPassThrough { get; set; } = 0.5m;

    /// <summary>Prima del precio internacional (FOB) sobre el precio local.</summary>
    public decimal GotoroPremium { get; set; } = 0.15m;

    /// <summary>Días hasta cobrar una exportación.</summary>
    public int ExportCollectionDays { get; set; } = 7;

    /// <summary>Fracción de lo exportado que igual presiona el precio local (vía oferta).</summary>
    public double ExportSupplyFactor { get; set; } = 0.3;

    /// <summary>Derechos de importación sobre las tiendas importadoras.</summary>
    public decimal ImportDuty { get; set; } = 0.20m;

    /// <summary>Tasa de estadística sobre lo importado.</summary>
    public decimal StatisticsFee { get; set; } = 0.03m;

    /// <summary>Tiendas que venden mercadería importada (ShopMenu.ShopId).</summary>
    public List<string> ImportShops { get; set; } = new();

    public decimal ExportDuty(string commodityId, decimal multiplier) =>
        ExportDuties.TryGetValue(commodityId, out decimal d) ? Math.Clamp(d * multiplier, 0, 0.9m) : 0;

    public decimal ImportSurcharge(decimal multiplier) => (ImportDuty * multiplier) + StatisticsFee;
}

/// <summary>Lote exportado a cobrar.</summary>
public sealed class ExportReceivable
{
    public long Id { get; set; }

    public string Company { get; set; } = "";

    public Time.EconomicDate ExportedOn { get; set; }

    public Time.EconomicDate CollectOn { get; set; }

    public decimal Amount { get; set; }
}
