namespace ZuzuMarket.Game;

/// <summary>Configuración del jugador (<c>config.json</c> en la carpeta del mod).</summary>
internal sealed class ModConfig
{
    /// <summary>Dificultad para partidas nuevas: "casual", "realista" o "argentina".
    /// Una partida ya iniciada conserva la dificultad con la que empezó.</summary>
    public string Difficulty { get; set; } = "realista";

    /// <summary>Mostrar las noticias económicas como aviso en pantalla.</summary>
    public bool NewsHud { get; set; } = true;

    /// <summary>Mandar cartas del Diario de Zuzu y del INDEF al buzón.</summary>
    public bool NewsLetters { get; set; } = true;

    /// <summary>Paga solo las boletas el día del vencimiento (o después, si estaban vencidas) cuando alcanza el oro.</summary>
    public bool AutoDebit { get; set; } = true;

    /// <summary>Tile del pueblo donde está el teléfono de Valle Inversiones (se mueve con <c>zuzu_place_phone</c>).</summary>
    public TilePosition PhonePosition { get; set; } = new() { X = 45, Y = 55 };

    /// <summary>
    /// Solo en dificultad Argentina: si es true, una quiebra puede rematar edificios de la granja (incluso con animales
    /// adentro, que se pierden). Apagado por defecto.
    /// </summary>
    public bool AllowBuildingAuction { get; set; }

    /// <summary>Agencia Bursátil en el pueblo (fachada e interior con Julián Ferreyra).</summary>
    public bool AgencyEnabled { get; set; } = true;

    /// <summary>
    /// Esquina superior izquierda de la fachada de la Agencia. Vacío = se busca un lugar libre automáticamente
    /// (distinto con Stardew Valley Expanded). Se elige con <c>zuzu_agency here</c>.
    /// </summary>
    public TilePosition? AgencyPosition { get; set; }

    /// <summary>Habilita comandos de depuración de consola, como <c>zuzu_reset</c> y <c>zuzu_event</c>.</summary>
    public bool DebugMode { get; set; }
}

internal sealed class TilePosition
{
    public int X { get; set; }

    public int Y { get; set; }
}