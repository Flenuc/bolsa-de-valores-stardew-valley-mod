# Bolsa de Valores de Zuzu — mod para Stardew Valley

Simulador económico-financiero para Stardew Valley: commodities con oferta y demanda, inflación,
sistema tributario argentino (nacional, provincial y municipal), empresas, y un mercado de capitales
con cauciones, letras, bonos, obligaciones negociables, acciones, futuros con entrega física y opciones.

📄 Ver el [documento de diseño](docs/DISENO.md).

**Estado:** Fase 3 — precios de mercado con oferta, estación, eventos e inflación; contabilidad real e impuestos
(Monotributo/RI, IVA, IIBB, Seguridad e Higiene, ABL, Ganancias); y ahora la **Bolsa de Zuzu**: cuenta comitente en
Valle Inversiones, 10 acciones con índice MERZU, Letras del Tesoro, cauciones, bonos soberanos (fijos, variables y CER)
y obligaciones negociables, con BCRF, curva de tasas, calificaciones, riesgo país y defaults; futuros MATZU con
garantías y entrega física en el Silo de Acopio, y opciones con compra y lanzamiento; desde el teléfono del pueblo o la
Terminal Bursátil fabricable. Requiere Stardew Valley 1.6 y SMAPI 4.x.

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
| `NewsLetters` | `true` | Manda cartas del Diario de Zuzu, del INDEF y del fisco al buzón. |
| `AutoDebit` | `true` | Paga solo las boletas el día del vencimiento (o las vencidas) si alcanza el oro. |
| `PhonePosition` | `{ "X": 45, "Y": 55 }` | Tile del pueblo donde está el teléfono de Valle Inversiones (se cambia con `zuzu_place_phone`). |
| `AllowBuildingAuction` | `false` | Solo en Argentina: una quiebra puede rematar edificios de la granja (incluso con animales adentro). |
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
| `zuzu_taxes` | Régimen, categoría, facturación y boletas pendientes y pagadas |
| `zuzu_results` | Estado de resultados acumulado |
| `zuzu_here` | Ubicación y tile actual (para ubicar el escritorio de pago) |
| `zuzu_pay [id]` | (solo `DebugMode`) paga boletas sin ir a la Municipalidad |
| `zuzu_regime ri\|mono` | (solo `DebugMode`) cambia el régimen impositivo |
| `zuzu_quotes` | Cotizaciones de BYMZ, Letras y tasas en este momento |
| `zuzu_portfolio` | Tu cartera en Valle Inversiones |
| `zuzu_rates` | Tasa del BCRF, curva soberana, riesgo país y calificaciones |
| `zuzu_bonds` | Bonos y obligaciones negociables con TIR, calificación y precio |
| `zuzu_default <soberano\|ticker>` | (solo `DebugMode`) fuerza el default de un emisor |
| `zuzu_futures [commodity]` | Futuros MATZU, posiciones y garantías |
| `zuzu_options <ticker>` | Cadena de opciones de una acción |
| `zuzu_silo [open]` | Contenido del Silo de Acopio (`open` abre el cofre; requiere `DebugMode`) |
| `zuzu_margin_call <commodity> <impacto>` | (solo `DebugMode`) mueve un precio y corre el control de garantías |
| `zuzu_credit` | Calificación crediticia, límite, préstamos y cauciones tomadoras |
| `zuzu_legal` | Situación legal: deuda exigible, embargo, concurso e inhabilitación |
| `zuzu_debt <monto>` | (solo `DebugMode`) crea una deuda en gestión de cobro para probar la escalera legal |
| `zuzu_companies` | Tipos de sociedad (y si están desbloqueados) y tus sociedades |
| `zuzu_company <id>` | Balance e impuestos impagos de una sociedad (p. ej. `emp1`) |
| `zuzu_exports` | Retenciones por commodity, contenedor de exportación y cobros pendientes |
| `zuzu_unlock_companies` | (solo `DebugMode`) desbloquea todos los tipos de sociedad |
| `zuzu_place_phone` | Mueve el teléfono de Valle Inversiones al tile que tenés enfrente (en el pueblo) |
| `zuzu_terminal` | (solo `DebugMode`) abre la Terminal Bursátil en cualquier lado |
| `zuzu_trade buy\|sell <especie> <cant>` | (solo `DebugMode`) opera sin la Terminal |
| `zuzu_rng <stream> [n]` | Valores del generador determinista para ese stream y día |
| `zuzu_reset confirm` | (solo `DebugMode`) reinicia la economía de la partida |
