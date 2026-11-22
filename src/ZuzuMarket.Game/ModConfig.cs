namespace ZuzuMarket.Game;

/// <summary>Configuración del jugador (<c>config.json</c> en la carpeta del mod).</summary>
internal sealed class ModConfig
{
    /// <summary>Dificultad para partidas nuevas: "casual", "realista" o "argentina".
    /// Una partida ya iniciada conserva la dificultad con la que empezó.</summary>
    public string Difficulty { get; set; } = "realista";

    /// <summary>Habilita comandos peligrosos de consola, como <c>zuzu_reset</c>.</summary>
    public bool DebugMode { get; set; }
}
