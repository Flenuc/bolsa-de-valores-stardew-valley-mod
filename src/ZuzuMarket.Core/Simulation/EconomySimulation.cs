using ZuzuMarket.Core.Commodities;
using ZuzuMarket.Core.Companies;
using ZuzuMarket.Core.Data;
using ZuzuMarket.Core.Events;
using ZuzuMarket.Core.Ledger;
using ZuzuMarket.Core.Legal;
using ZuzuMarket.Core.Macro;
using ZuzuMarket.Core.Markets;
using ZuzuMarket.Core.Random;
using ZuzuMarket.Core.State;
using ZuzuMarket.Core.Taxation;
using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Simulation;

/// <summary>Salida de log de la simulación (el mod la conecta al monitor de SMAPI).</summary>
public interface ISimulationLog
{
    void Info(string message);

    void Warn(string message);
}

public sealed class NullSimulationLog : ISimulationLog
{
    public static readonly NullSimulationLog Instance = new();

    public void Info(string message)
    {
    }

    public void Warn(string message)
    {
    }
}

/// <summary>Qué pasó al empezar un día (para que el juego muestre noticias y reaccione).</summary>
public sealed record DayStartedInfo(
    EconomicDate Date,
    bool IsBusinessDay,
    bool IsNewFiscalPeriod,
    bool IsNewFiscalYear,
    int DaysAdvanced,
    IReadOnlyList<EventDefinition> NewEvents,
    IReadOnlyList<InflationRecord> PublishedInflation,
    IReadOnlyList<TaxNotice> TaxNotices,
    IReadOnlyList<MarketNotice> MarketNotices,
    IReadOnlyList<AutoDebitPayment> AutoDebited,
    IReadOnlyList<RateDecision> RateDecisions,
    IReadOnlyList<CreditEvent> CreditEvents,
    LegalDay Legal,
    IReadOnlyList<CompanyNotice> CompanyNotices,
    decimal CompanyTransfers)
{
    /// <summary>Oro total que el juego tiene que descontarle al jugador por el débito automático.</summary>
    public long AutoDebitTotal => AutoDebited.Sum(p => p.Amount);
}

/// <summary>Boleta pagada por débito automático.</summary>
public sealed record AutoDebitPayment(long ObligationId, EconomicDate Date, long Amount);

/// <summary>
/// Orquestador: avanza el mundo económico un día por vez.
/// Orden de cada día: período fiscal (inflación, declaraciones y boletas) → eventos → IPC → precios → mora.
/// </summary>
public sealed class EconomySimulation
{
    public const string InflationStream = "inflacion";
    public const string EventsStream = "eventos";
    public const string CreditStream = "credito";

    /// <summary>Caída del sentimiento de una empresa que entra en default (≈ −78% en la acción).</summary>
    public const double DefaultEquityShock = -1.5;

    private readonly WorldState _state;
    private readonly ISimulationLog _log;

    private EconomySimulation(GameData data, WorldState state, GeneralLedger ledger, ISimulationLog log)
    {
        Data = data;
        _state = state;
        Ledger = ledger;
        _log = log;
        Inflation = new InflationModel(state.Inflation);
        Market = new CommodityMarket(data, state.Commodities);
        Events = new EventEngine(data, state.Events);
        Taxes = new TaxEngine(data, state.Taxes, ledger, Difficulty);
        Stocks = new StockMarket(data, state.Markets);
        CentralBank = new CentralBank(state.CentralBank, data.Markets.CentralBank);
        Credit = new CreditEngine(data, state.Markets.Issuers);
        Broker = new Broker(data, state.Markets, ledger, Stocks, CentralBank, Credit, Inflation, Market, Events, Taxes, () => Difficulty);
        Legal = new LegalEngine(data, state.Legal, ledger, Taxes, Broker, CentralBank, () => Difficulty);
        Companies = new PlayerCompanies(data, state.Companies, ledger, Taxes, Legal, Inflation, Market, () => Difficulty, Broker, Credit, Stocks);

        if (!Inflation.HasStarted)
        {
            // Partida nueva, o partida del esquema 1 (Fase 0) que recién migra.
            Inflation.StartPeriod(state.CurrentDate.FiscalPeriod, Difficulty, RandomStreams.For(state.Seed, state.CurrentDate, InflationStream));
        }

        RecalculatePrices();

        if (!CentralBank.Started)
        {
            // Partida nueva, o del esquema 4 que recién migra: primera tasa según la regla.
            CentralBank.Start(state.CurrentDate, Inflation.PeriodRate, Difficulty);
        }

        Credit.Initialize(Difficulty);

        if (!Stocks.Started)
        {
            // Partida nueva, o partida del esquema 3 que recién migra: la bolsa arranca hoy.
            Stocks.Start(state.CurrentDate, Market, Inflation.Index);
            Broker.IssueLetras(state.CurrentDate);
        }

        if (!state.Markets.BondsStarted)
        {
            Broker.IssueBonds(state.CurrentDate);
            state.Markets.BondsStarted = true;
        }

        if (state.Markets.Derivatives.ListedFor is null)
        {
            // Partida nueva, o del esquema 5 que recién migra: se listan futuros y opciones.
            Broker.ListDerivatives(state.CurrentDate);
        }
    }

    public GameData Data { get; }

    public GeneralLedger Ledger { get; }

    public InflationModel Inflation { get; }

    public CommodityMarket Market { get; }

    public EventEngine Events { get; }

    public TaxEngine Taxes { get; }

    public StockMarket Stocks { get; }

    public CentralBank CentralBank { get; }

    public CreditEngine Credit { get; }

    public LegalEngine Legal { get; }

    public PlayerCompanies Companies { get; }

    public Broker Broker { get; }

    public ulong Seed => _state.Seed;

    public EconomicDate CurrentDate => _state.CurrentDate;

    public EconomicDate CreatedOn => _state.CreatedOn;

    public DifficultyDefinition Difficulty =>
        Data.FindDifficulty(_state.DifficultyId)
        ?? throw new InvalidOperationException($"Dificultad desconocida: {_state.DifficultyId}.");

    /// <summary>Empieza a simular una partida (nueva, o existente en la que se instaló el mod).</summary>
    public static EconomySimulation StartNewGame(
        GameData data,
        ulong seed,
        EconomicDate date,
        string difficultyId,
        long openingCash,
        ISimulationLog? log = null)
    {
        DifficultyDefinition difficulty = data.FindDifficulty(difficultyId)
            ?? throw new ArgumentException($"Dificultad desconocida: {difficultyId}.", nameof(difficultyId));

        var state = new WorldState
        {
            Seed = seed,
            CreatedOn = date,
            CurrentDate = date,
            DifficultyId = difficulty.Id,
        };

        var ledger = new GeneralLedger(data.Chart, state.Ledger);
        ledger.AddEntity(LedgerEntities.Jugador);
        ledger.AddEntity(LedgerEntities.Granja);

        if (openingCash > 0)
        {
            ledger.Post(
                LedgerEntities.Jugador,
                date,
                "Asiento de apertura: oro inicial del jugador",
                EntrySources.Apertura,
                JournalLine.ToDebit(SystemAccounts.Caja, openingCash),
                JournalLine.ToCredit(SystemAccounts.Capital, openingCash));
        }

        return new EconomySimulation(data, state, ledger, log ?? NullSimulationLog.Instance);
    }

    /// <summary>Retoma una simulación guardada.</summary>
    public static EconomySimulation Load(GameData data, WorldState state, ISimulationLog? log = null)
    {
        if (data.FindDifficulty(state.DifficultyId) is null)
        {
            throw new ArgumentException($"La partida usa una dificultad que ya no existe: {state.DifficultyId}.", nameof(state));
        }

        var ledger = new GeneralLedger(data.Chart, state.Ledger);
        ledger.AddEntity(LedgerEntities.Jugador);
        ledger.AddEntity(LedgerEntities.Granja);
        return new EconomySimulation(data, state, ledger, log ?? NullSimulationLog.Instance);
    }

    /// <summary>Estado para guardar.</summary>
    public WorldState Snapshot() => _state;

    public DeterministicRandom Random(string streamName) => RandomStreams.For(_state.Seed, _state.CurrentDate, streamName);

    /// <summary>
    /// Se llama al empezar cada día del juego. Simula un paso por cada día transcurrido; los días salteados
    /// se simulan con clima soleado.
    /// </summary>
    /// <param name="autoDebitBudget">
    /// Oro disponible para el débito automático (<c>null</c> = desactivado). Se usa en cada día simulado, antes de
    /// aplicar la mora, así una boleta que vence se paga ese mismo día aunque se salteen días.
    /// El juego debe descontar <see cref="DayStartedInfo.AutoDebitTotal"/> del oro del jugador.
    /// </param>
    /// <param name="pocket">Oro del jugador: limita lo que se rinde a las sociedades (null = sin límite).</param>
    public DayStartedInfo OnDayStarted(EconomicDate date, Weather weather = Weather.Sun, FarmSnapshot? farm = null, long? autoDebitBudget = null, long? pocket = null)
    {
        farm ??= FarmSnapshot.Empty;
        _companyNotices = new List<CompanyNotice>();

        // Lo cobrado ayer por cuenta de las sociedades se les rinde antes que nada.
        decimal transfers = Companies.TransferCollections(_state.CurrentDate, pocket ?? autoDebitBudget);
        if (autoDebitBudget is long b)
        {
            autoDebitBudget = Math.Max(0, b - (long)Math.Ceiling(transfers));
        }

        _autoDebitBudget = autoDebitBudget;
        var autoDebited = new List<AutoDebitPayment>();
        _autoDebited = autoDebited;
        EconomicDate previous = _state.CurrentDate;
        int daysAdvanced = previous.DaysUntil(date);
        var newEvents = new List<EventDefinition>();
        var published = new List<InflationRecord>();
        var taxNotices = new List<TaxNotice>();
        var marketNotices = new List<MarketNotice>();
        _rateDecisions = new List<RateDecision>();
        _creditEvents = new List<CreditEvent>();
        _legalDay = new LegalDay();

        if (daysAdvanced < 0)
        {
            _log.Warn($"La fecha del juego ({date}) es anterior a la última fecha simulada ({previous}). Se toma la fecha del juego sin simular.");
            _state.CurrentDate = date;
        }
        else
        {
            if (daysAdvanced > 1)
            {
                _log.Warn($"Se saltearon {daysAdvanced - 1} día(s) entre {previous} y {date}; se simulan igual.");
            }

            for (int i = 1; i <= daysAdvanced; i++)
            {
                EconomicDate day = previous.AddDays(i);
                StepDay(day, i == daysAdvanced ? weather : Weather.Sun, farm, newEvents, published, taxNotices, marketNotices);
            }
        }

        if (!Taxes.Started)
        {
            // Primer día que se simula la partida (nueva o migrada): alta como monotributista, sin período de gracia.
            taxNotices.AddRange(Taxes.Start(_state.CurrentDate, farm, (decimal)Inflation.Index));
        }

        RunAutoDebit(_state.CurrentDate);
        _autoDebitBudget = null;
        _autoDebited = null;

        bool newPeriod = daysAdvanced > 0 && date.FiscalPeriod != previous.FiscalPeriod;
        bool newYear = daysAdvanced > 0 && date.Year != previous.Year;
        return new DayStartedInfo(date, date.IsBusinessDay, newPeriod, newYear, daysAdvanced, newEvents, published, taxNotices, marketNotices, autoDebited, _rateDecisions, _creditEvents, _legalDay, _companyNotices, transfers);
    }

    /// <summary>Se llama al terminar el día, antes de guardar.</summary>
    public void OnDayEnding()
    {
        // Fases siguientes: cierre de mercado, liquidaciones, devengamientos, impuestos, vencimientos.
    }

    /// <summary>Registra unidades vendidas por el jugador como oferta (sin contabilizar). Impactan en el precio del día siguiente.</summary>
    public bool RecordShipment(string itemId, int units) => Market.RecordShipment(itemId, units);

    /// <summary>
    /// Ventas del cajón de envíos al terminar el día: suman oferta y se contabilizan como venta a cobrar
    /// (el oro llega a la noche y se cobra al conciliar).
    /// </summary>
    public void RecordShipmentSales(IReadOnlyCollection<SoldItem> items) => RecordSales(items, SaleChannel.Shipping);

    /// <summary>Ventas directas a una tienda: suman oferta y se cobran en el momento.</summary>
    public void RecordShopSales(IReadOnlyCollection<SoldItem> items) => RecordSales(items, SaleChannel.Shop);

    /// <summary>Compra en una tienda (o a Robin) por el importe pagado.</summary>
    public JournalEntry? RecordPurchase(long gross) => Taxes.RecordPurchase(_state.CurrentDate, gross);

    /// <summary>
    /// Paga una boleta. Las del jugador, contra su Caja; las de una sociedad, con su caja y, si no alcanza, con un
    /// aporte del jugador por la diferencia. Devuelve el oro que el juego debe descontar, o null si no se pudo.
    /// </summary>
    public long? PayObligation(long obligationId)
    {
        if (Taxes.Find(obligationId) is not null)
        {
            return Taxes.Pay(obligationId, _state.CurrentDate);
        }

        if (FindObligation(obligationId) is not ({ } engine, { IsPaid: false } o) || Companies.Find(engine.Entity) is not { } company)
        {
            return null;
        }

        decimal shortfall = Math.Max(0, o.Total - Companies.Cash(company));
        long fromPocket = (long)Math.Ceiling(shortfall);
        if (fromPocket > 0)
        {
            Companies.Contribute(company.Id, fromPocket, decimal.MaxValue, _state.CurrentDate);
        }

        engine.Pay(obligationId, _state.CurrentDate);
        return fromPocket;
    }

    /// <summary>Oro del bolsillo que hace falta para pagar una boleta (las de una sociedad usan primero su caja).</summary>
    public long PocketNeeded(long obligationId)
    {
        if (FindObligation(obligationId) is not ({ } engine, { } o))
        {
            return 0;
        }

        return Companies.Find(engine.Entity) is { } company ? (long)Math.Ceiling(Math.Max(0, o.Total - Companies.Cash(company))) : o.Total;
    }

    /// <summary>Busca una boleta del jugador o de cualquiera de sus sociedades.</summary>
    public (TaxEngine Engine, TaxObligation Obligation)? FindObligation(long id)
    {
        if (Taxes.Find(id) is { } own)
        {
            return (Taxes, own);
        }

        foreach (CompanyState c in Companies.State.Companies)
        {
            if (Companies.TaxesOf(c).Find(id) is { } o)
            {
                return (Companies.TaxesOf(c), o);
            }
        }

        return null;
    }

    /// <summary>Boletas impagas del jugador y de sus sociedades activas.</summary>
    public IEnumerable<(TaxEngine Engine, TaxObligation Obligation)> AllUnpaid =>
        Taxes.Unpaid.Select(o => (Taxes, o))
            .Concat(Companies.Active.SelectMany(c => Companies.TaxesOf(c).Unpaid.Select(o => (Companies.TaxesOf(c), o))));

    /// <summary>
    /// Multiplicador del precio de venta vanilla: mercado × (1 − retención trasladada por el acopiador, precio FAS)
    /// × (1 + bonificación si una sociedad tuya vende ese rubro).
    /// </summary>
    public double SellMultiplier(string itemId, int category)
    {
        double multiplier = Market.GetMultiplierForItem(itemId);
        if (Data.FindCommodityByItemId(itemId) is { } commodity)
        {
            multiplier *= (double)(1 - (Data.Trade.ExportDuty(commodity.Id, Difficulty.TradeDuties) * Data.Trade.FasPassThrough));
        }

        return multiplier * (double)(1 + Companies.PriceBonus(Taxes.ActivityFor(itemId, category)));
    }

    /// <summary>Multiplicador de los precios de compra de una tienda: IPC × aranceles si importa.</summary>
    public double PurchaseMultiplier(string? shopId)
    {
        double multiplier = Inflation.Index;
        if (shopId is not null && Data.Trade.ImportShops.Contains(shopId, StringComparer.OrdinalIgnoreCase))
        {
            multiplier *= (double)(1 + Data.Trade.ImportSurcharge(Difficulty.TradeDuties));
        }

        return multiplier;
    }

    /// <summary>Hace empezar un evento hoy y recalcula los precios (comando de debug).</summary>
    public EventDefinition? ForceEvent(string eventId)
    {
        EventDefinition? e = Data.FindEvent(eventId);
        if (e is null)
        {
            return null;
        }

        Events.Start(e, _state.CurrentDate);
        if (e.InflationShock != 0)
        {
            Inflation.AddShock(e.InflationShock);
        }

        if (e.InflationShock > 0)
        {
            RecordDecision(CentralBank.EmergencyHike(_state.CurrentDate));
        }

        RecalculatePrices();
        return e;
    }

    /// <summary>
    /// Compara el oro real del jugador con la cuenta Caja y asienta la diferencia como movimiento no clasificado.
    /// Devuelve el asiento, o <c>null</c> si no había diferencia.
    /// </summary>
    /// <remarks>Primero cobra las ventas del cajón de envíos pendientes (Deudores por ventas).</remarks>
    public JournalEntry? ReconcileCash(long playerGold, EntryText description)
    {
        decimal cash = Ledger.GetBalance(LedgerEntities.Jugador, SystemAccounts.Caja);
        decimal difference = playerGold - cash;
        JournalEntry? last = null;

        decimal receivable = Ledger.GetBalance(LedgerEntities.Jugador, SystemAccounts.DeudoresPorVentas);
        if (difference > 0 && receivable > 0)
        {
            decimal collected = Math.Min(difference, receivable);
            last = Ledger.Post(
                LedgerEntities.Jugador,
                _state.CurrentDate,
                new EntryText("Cobro de ventas del cajón de envíos", "entry.receivable-collected"),
                EntrySources.Cobro,
                JournalLine.ToDebit(SystemAccounts.Caja, collected),
                JournalLine.ToCredit(SystemAccounts.DeudoresPorVentas, collected));
            difference -= collected;
        }

        if (difference == 0)
        {
            return last;
        }

        JournalLine[] lines = difference > 0
            ? new[]
            {
                JournalLine.ToDebit(SystemAccounts.Caja, difference),
                JournalLine.ToCredit(SystemAccounts.IngresosNoClasificados, difference),
            }
            : new[]
            {
                JournalLine.ToDebit(SystemAccounts.EgresosNoClasificados, -difference),
                JournalLine.ToCredit(SystemAccounts.Caja, -difference),
            };

        return Ledger.Post(LedgerEntities.Jugador, _state.CurrentDate, description, EntrySources.Conciliacion, lines);
    }

    private LegalDay _legalDay = new();
    private List<CompanyNotice> _companyNotices = new();
    private List<RateDecision> _rateDecisions = new();
    private List<CreditEvent> _creditEvents = new();

    /// <summary>
    /// Debug: mueve el precio de un commodity (impacto logarítmico) y corre el control de garantías,
    /// para probar llamadas de margen.
    /// </summary>
    public List<MarketNotice> ForceMarginTest(string commodityId, double logShock)
    {
        if (Data.FindCommodity(commodityId) is not { } commodity)
        {
            return new List<MarketNotice>();
        }

        Market.Get(commodity.Id).X += logShock;
        RecalculatePrices();
        Broker.SettleFutures(_state.CurrentDate);
        return Broker.CheckMargin(_state.CurrentDate);
    }

    /// <summary>El juego remató objetos o edificios por <paramref name="value"/>: se aplica a las deudas.</summary>
    public decimal ApplySeizure(decimal value) => Legal.ApplySeizure(value, _state.CurrentDate, new LegalDay());

    /// <summary>Cierra una quiebra declarada hoy (después de que el juego remató lo que pudo).</summary>
    public LegalDay FinalizeBankruptcy()
    {
        var day = new LegalDay();
        Legal.FinalizeBankruptcy(_state.CurrentDate, day);
        return day;
    }

    public LegalNotice? RequestConcurso()
    {
        var notices = new List<TaxNotice>();
        return Legal.RequestConcurso(_state.CurrentDate, notices);
    }

    /// <summary>Debug: crea una deuda vencida hace tanto que ya está en gestión de cobro.</summary>
    public TaxObligation CreateTestDebt(long amount)
    {
        var notices = new List<TaxNotice>();
        EconomicDate date = _state.CurrentDate;
        Ledger.Post(LedgerEntities.Jugador, date, new EntryText("Deuda de prueba", "entry.test-debt"), EntrySources.Ajuste,
            JournalLine.ToDebit(SystemAccounts.EgresosNoClasificados, amount),
            JournalLine.ToCredit(SystemAccounts.ImpuestosAPagarArf, amount));
        TaxObligation o = Taxes.CreateExternal(TaxKind.DeudaVarios, date.FiscalPeriod, amount, date, date, notices);
        o.Status = ObligationStatus.InCollection;
        o.LastAccrual = date;
        return o;
    }

    /// <summary>Fuerza el default de un emisor (comando de debug). Devuelve false si no existe o ya estaba en default.</summary>
    public bool ForceDefault(string issuer)
    {
        string id = issuer.Equals("soberano", StringComparison.OrdinalIgnoreCase) ? CreditEngine.Sovereign : issuer.ToUpperInvariant();
        CreditEvent? e = Credit.ForceDefault(id, _state.CurrentDate);
        if (e is null)
        {
            return false;
        }

        ApplyCreditEvent(e, _state.CurrentDate);
        return true;
    }

    private void RecordDecision(RateDecision decision)
    {
        string direction = decision.Change > 0.00001 ? "up" : decision.Change < -0.00001 ? "down" : "hold";
        Events.AddNews(new NewsItem(decision.Date, NewsKind.RateDecision, direction, decision.Rate));
        _rateDecisions.Add(decision);
    }

    private void ApplyCreditEvent(CreditEvent e, EconomicDate day)
    {
        _creditEvents.Add(e);
        switch (e.Kind)
        {
            case CreditEventKind.Default:
                Events.AddNews(new NewsItem(day, NewsKind.Default, e.Issuer, 0));
                Stocks.Shock(e.Issuer, DefaultEquityShock);
                break;
            case CreditEventKind.Restructuring:
                Events.AddNews(new NewsItem(day, NewsKind.Restructuring, e.Issuer, 0));
                break;
            default:
                Events.AddNews(new NewsItem(day, NewsKind.RatingChange, $"{e.Issuer}:{e.Rating}", e.Kind == CreditEventKind.Upgrade ? 1 : -1));
                break;
        }
    }

    /// <summary>Al empezar la estación: BCRF, calificaciones, defaults, canjes, pagos de bonos y licitaciones.</summary>
    private void OnMarketsPeriodStart(EconomicDate day, DifficultyDefinition difficulty, List<MarketNotice> marketNotices)
    {
        RecordDecision(CentralBank.Meet(day, Inflation.PeriodRate, difficulty));

        foreach (CreditEvent e in Credit.OnPeriodStart(day, difficulty, RandomStreams.For(_state.Seed, day, CreditStream)))
        {
            ApplyCreditEvent(e, day);
        }

        foreach ((string issuer, IssuerState s) in Credit.Issuers.Where(kv => kv.Value.Defaulted).ToList())
        {
            if (s.DefaultedIn is FiscalPeriod since && day.FiscalPeriod.SeasonsSince(since) >= Data.Ratings.RestructuringDelay)
            {
                marketNotices.AddRange(Broker.Restructure(issuer, day));
                ApplyCreditEvent(Credit.Restructure(issuer), day);
            }
        }

        marketNotices.AddRange(Broker.ProcessBondPayments(day));
        marketNotices.Add(Broker.IssueLetras(day));
        if (day.Season == Season.Spring)
        {
            Broker.IssueBonds(day);
        }

        Broker.ListDerivatives(day);
    }

    private long? _autoDebitBudget;
    private List<AutoDebitPayment>? _autoDebited;

    /// <summary>Paga lo que vence (o venció) hasta ese día, mientras alcance el presupuesto.</summary>
    private void RunAutoDebit(EconomicDate day)
    {
        if (_autoDebitBudget is not long budget || _autoDebited is null || !Taxes.Started)
        {
            return;
        }

        foreach (TaxObligation o in Taxes.DueOn(day))
        {
            if (budget < o.Total)
            {
                break;
            }

            if (Taxes.Pay(o.Id, day) is long paid)
            {
                budget -= paid;
                _autoDebited.Add(new AutoDebitPayment(o.Id, day, paid));
            }
        }

        _autoDebitBudget = budget;
    }

    private void RecordSales(IReadOnlyCollection<SoldItem> items, SaleChannel channel)
    {
        foreach (SoldItem item in items)
        {
            Market.RecordShipment(item.ItemId, item.Units);
        }

        Taxes.RecordSales(_state.CurrentDate, Companies.Attribute(items, channel, _state.CurrentDate), channel);
    }

    private void StepDay(EconomicDate day, Weather weather, FarmSnapshot farm, List<EventDefinition> newEvents, List<InflationRecord> published, List<TaxNotice> taxNotices, List<MarketNotice> marketNotices)
    {
        _state.CurrentDate = day;
        DifficultyDefinition difficulty = Difficulty;

        if (_state.Inflation.CurrentPeriod != day.FiscalPeriod)
        {
            // Cierre de la estación: dividendos con la UPA del último cierre.
            marketNotices.AddRange(Broker.PayDividends(day));

            InflationRecord? record = Inflation.ClosePeriod();
            if (record is not null)
            {
                published.Add(record);
                Events.AddNews(new NewsItem(day, NewsKind.Inflation, record.Period.ToString(), record.Rate));
            }

            Inflation.StartPeriod(day.FiscalPeriod, difficulty, RandomStreams.For(_state.Seed, day, InflationStream));
            OnMarketsPeriodStart(day, difficulty, marketNotices);
        }

        if (Taxes.Started)
        {
            bool newPeriod = Taxes.State.Current.Period != day.FiscalPeriod;
            taxNotices.AddRange(Taxes.OnPeriodStart(day, farm, (decimal)Inflation.Index));
            if (newPeriod)
            {
                Legal.IssueLoanInstallments(day, taxNotices);
                Legal.UpdateRating(day, _legalDay.Notices);
                Companies.OnPeriodStart(day, _companyNotices);
            }
        }

        Events.Expire(day);
        foreach (EventDefinition e in Events.Roll(day, weather, difficulty, RandomStreams.For(_state.Seed, day, EventsStream)))
        {
            newEvents.Add(e);
            if (e.InflationShock != 0)
            {
                Inflation.AddShock(e.InflationShock);
            }

            if (e.InflationShock > 0)
            {
                RecordDecision(CentralBank.EmergencyHike(day));
            }
        }

        Inflation.AdvanceDay();
        Market.Step(day, Inflation.Index, (double)difficulty.PriceVolatility, _state.Seed, c => Events.PriceImpact(c, day));
        Stocks.Step(day, Market, Inflation.Index, (double)difficulty.PriceVolatility, _state.Seed);
        marketNotices.AddRange(Broker.OnDay(day));
        marketNotices.AddRange(Broker.SettleRepos(day));
        if (day.IsBusinessDay)
        {
            // Derivados al cierre: diferencias diarias, entregas y ejercicios que vencen, control de garantías.
            Broker.SettleFutures(day);
            marketNotices.AddRange(Broker.DeliverFutures(day));
            marketNotices.AddRange(Broker.ExpireOptions(day));
            if (Broker.HasDerivativePositions || Broker.MarginBalance != 0)
            {
                marketNotices.AddRange(Broker.CheckMargin(day));
            }
        }
        RunAutoDebit(day);
        int before = taxNotices.Count;
        taxNotices.AddRange(Taxes.OnDay(day));
        Legal.RecordTaxNotices(taxNotices.Skip(before));
        Legal.OnDay(day, _legalDay);
        Companies.OnDay(day, q => Broker.DerivativesRules.QualityPremium(q), _companyNotices);
    }

    private void RecalculatePrices()
    {
        EconomicDate today = _state.CurrentDate;
        Market.Recalculate(today, Inflation.Index, c => Events.PriceImpact(c, today));
    }
}
