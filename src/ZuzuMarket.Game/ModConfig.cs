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

    /// <summary>Habilita comandos de depuración de consola, como <c>zuzu_reset</c> y <c>zuzu_event</c>.</summary>
    public bool DebugMode { get; set; }
}
