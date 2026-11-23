# Cómo publicar en Nexus Mods

## 1. Generar el archivo
```powershell
dotnet build src\ZuzuMarket.Game -c Release
```
El zip queda en `src\ZuzuMarket.Game\bin\Release\net6.0\ZuzuMarket 1.0.0.zip` (contiene la carpeta `ZuzuMarket`
con el manifest, las DLL, `data`, `assets` e `i18n`). Ese es el archivo que se sube. Cerrá el juego antes de compilar.

## 2. Datos de la página
| Campo | Valor |
|---|---|
| Nombre | Zuzu Stock Exchange - Bolsa de Valores de Zuzu |
| Versión | 1.0.0 |
| Categoría | Gameplay Mechanics (o Economy, si existe en la sección) |
| Resumen corto | ver `summary.txt` |
| Descripción | pegar `page.bbcode` en el editor (modo BBCode) |
| Idioma | English (con sección en español) |
| Requisitos | SMAPI; opcional: Generic Mod Config Menu |
| Etiquetas sugeridas | Economy, Gameplay, Overhaul, Money, Taxes, NPC, Spanish, SVE compatible |
| Permisos | Código abierto bajo licencia MIT (podés marcar "users can modify/convert/use assets" con crédito) |

## 3. Capturas sugeridas (en este orden)
1. La Terminal, pestaña Panel (MERZU, noticias y cotizaciones).
2. Pestaña Acciones con el gráfico de una empresa.
3. Pestaña Renta fija con los bonos.
4. La fachada de la Agencia en el pueblo.
5. Julián en la Agencia, hablando (con su retrato).
6. La pestaña Empresas con una sociedad y el Mercado de capitales.
7. La pestaña Dólar en modo Argentina (con brecha).
8. Una carta del fisco o del juzgado.
9. Krobus vendiendo billetes gotoro.
Recomendado: imagen principal de 1280×720 o más; capturas sin la consola de SMAPI.

## 4. Después de subirlo
Nexus asigna un número al mod (aparece en la URL: `nexusmods.com/stardewvalley/mods/NÚMERO`). Agregalo al manifest
para que SMAPI avise de las actualizaciones, y publicá una 1.0.1 cuando haga falta:
```json
"UpdateKeys": [ "Nexus:NÚMERO", "GitHub:Flenuc/bolsa-de-valores-stardew-valley-mod" ]
```

## 5. Antes de cada versión
- `dotnet test` en verde.
- Subir la versión en `manifest.json` y anotar los cambios en `CHANGELOG.md`.
- Probar una partida nueva y una existente (y, si se puede, con Stardew Valley Expanded).
