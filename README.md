# Bolsa de Valores de Zuzu — mod para Stardew Valley

Simulador económico-financiero para Stardew Valley: commodities con oferta y demanda, inflación,
sistema tributario argentino (nacional, provincial y municipal), empresas, y un mercado de capitales
con cauciones, letras, bonos, obligaciones negociables, acciones, futuros con entrega física y opciones.

📄 Ver el [documento de diseño](docs/DISENO.md).

**Estado:** Fase 1 — los 15 commodities tienen precio de mercado (oferta por tus ventas, estación, eventos e
inflación) que reemplaza el precio de venta vanilla; las tiendas suben con la inflación; los eventos llegan como
noticias y cartas. Requiere Stardew Valley 1.6 y SMAPI 4.x.

## Compilar

Requisitos: [.NET 8 SDK](https://dotnet.microsoft.com/download) y el juego instalado con SMAPI.

```powershell
dotnet test                              # tests de la lógica (no necesitan el juego)
dotnet build src/ZuzuMarket.Game         # compila y copia el mod a Stardew Valley/Mods/ZuzuMarket
dotnet build src/ZuzuMarket.Game -c Release   # además genera el .zip para distribuir
```

Si el juego no está en la ruta estándar de Steam/GOG, agregá `<GamePath>` en `src/ZuzuMarket.Game/ZuzuMarket.Game.csproj`
(ver [ModBuildConfig](https://smapi.io/package/readme)).

## Configuración (`Mods/ZuzuMarket/config.json`)

| Opción | Por defecto | Descripción |
|---|---|---|
| `Difficulty` | `realista` | `casual`, `realista` o `argentina`. Se aplica a partidas nuevas; una partida ya iniciada conserva la suya. |
| `NewsHud` | `true` | Muestra las noticias económicas como aviso en pantalla. |
| `NewsLetters` | `true` | Manda cartas del Diario de Zuzu y del INDEF al buzón. |
| `DebugMode` | `false` | Habilita comandos de depuración como `zuzu_reset` y `zuzu_event`. |

## Comandos de consola (SMAPI)

| Comando | Descripción |
|---|---|
| `zuzu_status` | Fecha económica, día hábil, período fiscal, dificultad, semilla y conciliación Caja ↔ oro |
| `zuzu_ledger [n]` | Últimos `n` asientos del libro diario |
| `zuzu_balance [entidad]` | Balance de sumas y saldos (`jugador` o `granja`) |
| `zuzu_prices [commodity]` | Precios de mercado de hoy; con un id (p. ej. `chirivia`), detalle e historial |
| `zuzu_inflation` | IPC-F, inflación mensual vigente e historial publicado por el INDEF |
| `zuzu_events` | Eventos en curso y últimas noticias |
| `zuzu_event <id>` | (solo `DebugMode`) hace empezar un evento hoy |
| `zuzu_rng <stream> [n]` | Valores del generador determinista para ese stream y día |
| `zuzu_reset confirm` | (solo `DebugMode`) reinicia la economía de la partida |
