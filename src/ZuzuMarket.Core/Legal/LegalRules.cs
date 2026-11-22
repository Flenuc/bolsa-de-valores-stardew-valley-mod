using ZuzuMarket.Core.Time;

namespace ZuzuMarket.Core.Legal;

/// <summary>Reglas de embargos, concurso, quiebra, préstamos y caución tomadora (<c>data/legal.json</c>).</summary>
public sealed class LegalRules
{
    /// <summary>Días en gestión de cobro hasta el embargo (÷ velocidad de quiebra de la dificultad).</summary>
    public int EmbargoAfterDays { get; set; } = 7;

    /// <summary>Días en gestión de cobro hasta el secuestro de bienes.</summary>
    public int SeizureAfterDays { get; set; } = 14;

    /// <summary>Días entre secuestros sucesivos mientras siga la deuda.</summary>
    public int SeizureEveryDays { get; set; } = 7;

    /// <summary>Días en gestión de cobro hasta la quiebra (si la dificultad la habilita).</summary>
    public int BankruptcyAfterDays { get; set; } = 28;

    /// <summary>Fracción de cada cobro del cajón de envíos que se embarga.</summary>
    public decimal IncomeGarnishRate { get; set; } = 0.30m;

    /// <summary>Valor de remate de los objetos secuestrados, sobre su precio de venta.</summary>
    public decimal SeizureValueRate { get; set; } = 0.5m;

    /// <summary>Valor de remate de un edificio, sobre su costo de construcción.</summary>
    public decimal BuildingAuctionRate { get; set; } = 0.5m;

    public int ConcursoInstallments { get; set; } = 4;

    /// <summary>Quita del concurso según la calificación del jugador.</summary>
    public Dictionary<string, decimal> ConcursoHaircut { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Oro del bolsillo que la quiebra no toca (mínimo inembargable).</summary>
    public decimal BankruptcyCashExempt { get; set; } = 500m;

    /// <summary>Estaciones de inhabilitación después de una quiebra (÷ velocidad).</summary>
    public int InhabilitationSeasons { get; set; } = 4;

    public List<int> LoanTerms { get; set; } = new() { 2, 4, 8 };

    /// <summary>Margen del banco sobre la tasa de política (más el spread de la calificación del jugador).</summary>
    public double BankSpread { get; set; } = 0.01;

    /// <summary>Límite de crédito = (fracción de la facturación anual + fracción del patrimonio) × factor de calificación.</summary>
    public decimal LoanRevenueShare { get; set; } = 0.5m;

    public decimal LoanEquityShare { get; set; } = 0.3m;

    public Dictionary<string, decimal> LoanFactor { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public decimal LoanMinimum { get; set; } = 100m;

    public List<int> RepoTerms { get; set; } = new() { 7, 14, 28 };

    /// <summary>Máximo de la caución tomadora como fracción del valor de los títulos.</summary>
    public decimal RepoMaxRatio { get; set; } = 0.5m;

    public double RepoSpread { get; set; } = 0.01;

    public decimal Haircut(string rating) => ConcursoHaircut.TryGetValue(rating, out decimal h) ? h : 0.3m;

    public decimal Factor(string rating) => LoanFactor.TryGetValue(rating, out decimal f) ? f : 0;
}

public enum LegalEventKind
{
    Overdue,
    Intimation,
    Embargo,
    Concurso,
    Bankruptcy,

    /// <summary>Quebró una sociedad del jugador (responsabilidad limitada, pero mancha el historial).</summary>
    CompanyBankruptcy,
}

public sealed record LegalEvent(EconomicDate Date, LegalEventKind Kind);

public sealed class Loan
{
    public long Id { get; set; }

    public decimal Principal { get; set; }

    /// <summary>Tasa por estación.</summary>
    public double Rate { get; set; }

    public int TermSeasons { get; set; }

    public EconomicDate TakenOn { get; set; }

    public int InstallmentsIssued { get; set; }

    public decimal Outstanding { get; set; }

    /// <summary>Cuota fija (sistema francés) en oro.</summary>
    public long Installment { get; set; }
}

public sealed class LegalState
{
    /// <summary>Calificación crediticia del jugador.</summary>
    public string Rating { get; set; } = "BB";

    public double Score { get; set; } = 50;

    public List<LegalEvent> Events { get; set; } = new();

    /// <summary>Primer día con deudas en gestión de cobro (null si no hay).</summary>
    public EconomicDate? CollectionSince { get; set; }

    public EconomicDate? EmbargoSince { get; set; }

    public EconomicDate? LastSeizure { get; set; }

    public bool ConcursoActive { get; set; }

    public EconomicDate? InhabilitadoUntil { get; set; }

    /// <summary>La quiebra se declaró y espera que el juego remate bienes antes de condonar el resto.</summary>
    public bool BankruptcyPending { get; set; }

    public int Bankruptcies { get; set; }

    public List<Loan> Loans { get; set; } = new();

    public long NextLoanId { get; set; } = 1;
}

public enum LegalNoticeKind
{
    EmbargoStarted,
    EmbargoLifted,
    AccountSeized,
    IncomeGarnished,
    GoodsSeized,
    SeizureRequested,
    ConcursoOpened,
    ConcursoCompleted,
    BankruptcyDeclared,
    BankruptcyClosed,
    Rehabilitated,
    LoanTaken,
    RatingChanged,
    CashSeized,
}

public sealed record LegalNotice(LegalNoticeKind Kind, EconomicDate Date, decimal Amount = 0, string? Detail = null);

public sealed record LoanQuote(decimal Amount, int TermSeasons, double Rate, long Installment, decimal Limit, Markets.TradeError Error);
