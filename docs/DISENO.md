# Documento de Diseño — Bolsa de Valores de Zuzu (mod para Stardew Valley)

> Estado: **borrador v0.1** · Plataforma: Stardew Valley 1.6 + SMAPI 4.x · Lenguaje: C# (.NET 6) + Harmony

---

## 1. Visión

Convertir Stardew Valley en un simulador económico-financiero: los recursos del juego se comportan como
**commodities** cuyo precio surge de la **oferta y la demanda** y de **eventos**; la granja es una
**empresa agropecuaria** con contabilidad, obligaciones fiscales y acceso al **mercado de capitales**;
más adelante el jugador puede fundar otras empresas, emitir deuda y hasta abrir su capital en bolsa.

La economía afecta los **precios reales del juego** (venta en el cajón de envíos y compra en tiendas), hay
**inflación**, el jugador puede sufrir **embargos** e incluso **quebrar**.

### 1.1 Decisiones tomadas

| Tema | Decisión |
|---|---|
| Terminología | **Argentina** (IVA, Ganancias, Ingresos Brutos, ABL, retenciones, cauciones, ON, etc.) |
| Impacto en el juego | Los precios del mercado **reemplazan** los precios de venta/compra vanilla |
| Futuros | Con **entrega física** de mercadería |
| Inflación | **Sí**, con índice de precios propio |
| Quiebra y embargos | **Sí** |
| Dificultad | Niveles configurables: **Casual / Realista / Argentina** |
| Multijugador | **Un jugador primero**; arquitectura preparada para multijugador (host autoritativo) |
| Acceso a la terminal | **Ambas**: edificio en Pelican Town y terminal fabricable para la granja |

### 1.2 Pilares de diseño

1. **Coherencia económica**: todo flujo de dinero pasa por un libro contable de partida doble; nada aparece de la nada.
2. **Consecuencias legibles**: cada cambio de precio o impuesto se explica con una noticia, un recibo o un asiento.
3. **Datos antes que código**: commodities, empresas, eventos e impuestos se definen en JSON para poder balancear sin recompilar.
4. **Determinismo**: el azar usa semilla por partida + día, para que las partidas sean reproducibles y testeables.
5. **Jugable antes que perfecto**: cada fase debe dejar algo divertido; la complejidad se desbloquea de a poco.

---

## 2. Ambientación (lore)

Para evitar usar instituciones reales, se usan **términos** argentinos con **instituciones** adaptadas al mundo de Ferngill.

| Nivel / Rol | Entidad en el juego | Equivalente argentino |
|---|---|---|
| Nación | República de Ferngill | Estado Nacional |
| Recaudador nacional | **ARF** — Agencia de Recaudación de Ferngill | ARCA / ex-AFIP |
| Banco central | **BCRF** — Banco Central de la República de Ferngill | BCRA |
| Regulador | **CNV** — Comisión Nacional de Valores de Ferngill | CNV |
| Provincia | **Provincia del Valle** | Provincia |
| Recaudador provincial | **ARPV** — Agencia de Rentas del Valle | ARBA / AGIP |
| Municipio | **Municipalidad de Pelican Town** (intendente Lewis) | Municipio |
| Bolsa | **BYMZ** — Bolsas y Mercados de Zuzu | BYMA |
| Índice líder | **MERZU** | MERVAL |
| Mercado de futuros | **MATZU** — Mercado a Término de Zuzu | Matba-Rofex |
| Exterior | Imperio Gotoro | Socio comercial / resto del mundo |
| Índice de precios | **IPC-F** (INDEF) | IPC (INDEC) |
| Coeficiente de ajuste | **CER-F** | CER |

### 2.1 Calendario económico

| Concepto del juego | Equivalente económico |
|---|---|
| 1 día | 1 rueda de operaciones (lunes a viernes; **sáb. y dom. el mercado cierra**) |
| Horario de mercado | 9:00 a 17:00 hora del juego (precios intradiarios cada 10 min) |
| 1 estación (28 días) | 1 **período fiscal** (≈ "mes" económico: IVA, IIBB, inflación mensual) |
| 1 año (4 estaciones) | 1 **ejercicio fiscal** (Ganancias, Bienes Personales, balance anual) |

---

## 3. Arquitectura técnica

```
src/
├── ZuzuMarket.Core/        ← lógica pura, SIN referencias al juego (net6.0, testeable en CI)
│   ├── Time/               calendario económico, días hábiles, vencimientos
│   ├── Random/             RNG determinista (semilla partida + día)
│   ├── Ledger/             contabilidad por partida doble, plan de cuentas
│   ├── Macro/              inflación, tasa de política, tipo de cambio (modo Argentina)
│   ├── Commodities/        oferta, demanda, formación de precios
│   ├── Events/             motor de eventos y noticias
│   ├── Companies/          empresas ficticias y empresas del jugador
│   ├── Taxation/           motor tributario definido por reglas
│   ├── Markets/            instrumentos, valuación, órdenes, márgenes, liquidación
│   ├── Legal/              deudas, intimaciones, embargos, concurso, quiebra
│   └── Simulation/         orquestador: avanza el mundo un tick / un día
├── ZuzuMarket.Game/        ← mod SMAPI: integración con el juego
│   ├── Hooks/              parches Harmony (precios de venta, tiendas, cajón de envíos)
│   ├── Bridges/            mapeo objetos del juego ↔ commodities, dinero ↔ cuenta "Caja"
│   ├── Content/            terminal fabricable, edificio, correo, TV, diario
│   ├── UI/                 menús de la terminal
│   ├── Persistence/        guardado por partida (helper.Data.WriteSaveData)
│   └── Commands/           comandos de consola para debug
├── ZuzuMarket.Core.Tests/  ← xUnit: tests de la lógica (se corren sin el juego)
data/                       ← JSON: commodities, empresas, eventos, impuestos, dificultades
i18n/                       ← es.json (principal), en.json
```

**Regla de oro:** `Core` no conoce Stardew Valley. `Game` traduce eventos del juego (venta, compra, cambio de día)
en comandos para `Core`, y aplica los resultados (precios, cobros, embargos) al juego.

### 3.1 Ciclo de simulación

```
DayStarted ─► Macro (inflación diaria, tasa) ─► Eventos del día ─► Apertura de mercado
   │
   ├─ cada 10 min (9:00–17:00): ticks de precios intradiarios, ejecución de órdenes límite
   │
DayEnding ─► Ventas del cajón de envíos (oferta) ─► Cierre de mercado ─► Liquidación T+0/T+1
          ─► Diferencias diarias de futuros (margin calls) ─► Devengamiento (intereses, cupones)
          ─► Impuestos devengados ─► Vencimientos legales (intimaciones, embargos) ─► Guardado
Fin de estación ─► Cierre fiscal mensual (IVA, IIBB, ABL, anticipos) ─► Dividendos ─► Inflación mensual publicada
Fin de año ─► Ganancias, Bienes Personales, balance anual, calificaciones crediticias
```

### 3.2 Persistencia

Todo el estado de `Core` se serializa en un único objeto `WorldState` guardado con SMAPI por partida.
Versión de esquema incluida para migraciones.

### 3.3 Multijugador (futuro)

El host corre la simulación; los granjeros remotos envían órdenes por mensajes SMAPI y reciben snapshots.
Cada granjero tendrá su propia cuenta comitente y su propia empresa (o socios en la misma).

---

## 4. Contabilidad (Ledger)

- **Partida doble** obligatoria: cada operación genera un asiento balanceado.
- Entidades contables: el jugador (persona humana), cada empresa del jugador, y el Estado (para recaudación).
- Cuentas principales: Caja (= oro del jugador en el juego), Cuenta comitente, Banco, Mercaderías (inventario
  valuado), Bienes de uso (edificios, herramientas), Inversiones, Créditos fiscales (IVA CF, retenciones sufridas),
  Deudas fiscales, Deudas financieras, Garantías (márgenes), Patrimonio neto, Ventas, Costos, Gastos, Resultados
  financieros, Diferencia de inflación (RECPAM simplificado).
- Reportes: Libro diario, Mayor, Estado de Resultados, Balance General, Flujo de fondos.

**Puente con el juego:** el oro del jugador (`Game1.player.Money`) es la cuenta "Caja". Todo cambio de oro que no
haya originado el mod (compra en tienda, venta, regalo) se detecta y se asienta automáticamente.

---

## 5. Macroeconomía

### 5.1 Inflación
- Índice **IPC-F** que crece diariamente según una inflación mensual objetivo + shocks de eventos
  (emisión, devaluación, cosecha récord, etc.).
- Afecta: precios base de los commodities, precios de **tiendas** (Pierre, Joja, herrero, Robin…), alícuotas fijas
  (ABL), salarios simulados de empresas, y el costo de los bonos ajustables por CER-F.
- El jugador ve precio **nominal** y **real** (a moneda constante del año 1).

### 5.2 Tasa de política monetaria (BCRF)
- Tasa de referencia que el BCRF ajusta cada estación según inflación (regla de Taylor simplificada) y eventos.
- Determina: tasa de cauciones, rendimiento de letras, piso de la curva de bonos, costo del crédito.

### 5.3 Tipo de cambio (solo modo Argentina, fase tardía)
- Moneda extranjera: **oro gotoro**. Exportaciones liquidadas a tipo de cambio oficial; brecha con el "blue",
  cepo, devaluaciones como eventos.

---

## 6. Commodities: oferta y demanda

### 6.1 Agrupación
Cada objeto vendible pertenece a un commodity. Ejemplos iniciales:

| Commodity | Objetos incluidos | Unidad de contrato futuro |
|---|---|---|
| Chirivía, Papa, Coliflor, Melón, Calabaza, etc. | Cada cultivo es un commodity propio | 100 u. |
| Trigo, Maíz | — (granos, núcleo del mercado de futuros) | 500 u. |
| Leche, Huevos, Lana | Productos animales | 100 u. |
| Cobre, Hierro, Oro, Iridio | Minerales y lingotes | 100 u. |
| Pescado (por especie o canasta) | Peces | 50 u. |
| Vino, Queso, Mermeladas | Productos artesanales | 50 u. |

La **calidad** (normal, plata, oro, iridio) mantiene el multiplicador vanilla sobre el precio del commodity.

### 6.2 Modelo de precio

```
P(t) = P_base · IPC(t) · F_estación(t) · exp(x(t))

dx = -θ · x · dt  +  α · (D(t) - S(t)) / Volumen_normal  +  σ · ε  +  Saltos_por_eventos
```

- `P_base`: precio vanilla del objeto.
- `F_estación`: los cultivos fuera de estación son más escasos (más caros); en cosecha, más baratos.
- `θ`: reversión a la media (el mercado "se recupera").
- **Oferta `S`**: ventas del jugador (cajón de envíos) + producción simulada de empresas ficticias. Lo vendido se
  acumula en un "stock de mercado" que decae con los días. *Si vendés 2.000 chirivías, el precio se hunde.*
- **Demanda `D`**: consumo base + demanda de empresas (p. ej., la destilería compra uva) + eventos (festivales,
  exportaciones a Gotoro, plagas en otras regiones).
- `σ`: volatilidad propia de cada commodity (minerales bajos, pescado alto…).

### 6.3 Aplicación al juego
- Parche Harmony en el precio de venta de `StardewValley.Object` → precio de mercado actual.
- La venta del cajón se liquida **al precio de cierre**, neta de **retenciones** (derechos de exportación) en los
  commodities que corresponda (precio "FAS" del acopiador).
- Los precios de compra en tiendas siguen el IPC-F + aranceles de importación sobre ítems importados.

---

## 7. Motor de eventos

Un evento tiene: condiciones (estación, clima, año, hitos, dificultad), probabilidad, duración, efectos y una noticia.

| Tipo | Ejemplos | Efectos |
|---|---|---|
| Climáticos | Sequía en verano, inundación, helada temprana | Oferta ↓ de ciertos cultivos |
| Agro | Plaga, cosecha récord en Gotoro | Precio ↑/↓ |
| Corporativos | Balance sorpresa, fusión, fraude, huelga | Acción ↑/↓, calificación |
| Macro | Suba de tasa, emisión, devaluación, corrida | Inflación, tasas, todo el mercado |
| Fiscales | Reforma tributaria, moratoria, blanqueo, nueva tasa municipal | Alícuotas |
| Soberanos | Reestructuración de deuda, **default** | Bonos |
| Juego | Centro Comunitario completado / Joja | Empresas, infraestructura, demanda |

Canales de noticias: **TV** (canal financiero nuevo), **correo** (ARF, intimaciones, avisos de la CNV),
**diario financiero** en la terminal, y un ticker en la HUD (opcional).

---

## 8. Empresas

### 8.1 Empresas ficticias (cotizan en BYMZ)
Cada empresa tiene: actividad, commodities que produce/consume, ingresos, costos, deuda, acciones en circulación,
política de dividendos, **calificación crediticia** (AAA…D) y volatilidad.

Ejemplos iniciales (≈10):

| Ticker | Empresa | Actividad | Sensible a |
|---|---|---|---|
| JOJA | Joja Corporation | Retail | Consumo, Centro Comunitario vs. Joja |
| COAV | Cooperativa Agrícola del Valle | Agro | Cultivos |
| CALI | Minera Calico | Minería | Minerales |
| GEMS | Pesquera Mar de las Gemas | Pesca | Pescado |
| FCVA | Ferrocarril del Valle | Transporte | Volumen comerciado |
| ZUEN | Zuzu Energía | Energía | Inflación, regulación |
| BZUZ | Banco de Zuzu | Finanzas | Tasa de política |
| GOTR | Gotoro Trading | Comercio exterior | Retenciones, tipo de cambio |
| DEST | Destilería del Valle | Bebidas | Uva, lúpulo, trigo |
| CONS | Constructora Zuzu | Construcción | Madera, piedra, tasas |

**Precio de la acción:** `Valor fundamental (P/E del sector × utilidad esperada)` × `exp(sentimiento)`, donde la
utilidad depende de los precios de los commodities que la empresa produce y consume, y el sentimiento es un proceso
con reversión a la media + eventos. Balances trimestrales (por estación) con sorpresas.

### 8.2 Empresas del jugador
- **La granja**: arranca como **monotributista**. Al superar el tope de facturación anual, debe pasar a
  **Responsable Inscripto** (IVA, Ganancias) y puede constituir una **S.A.** (requisito para emitir ON y abrir capital).
- **Nuevas empresas** (desbloqueables por capital mínimo, licencia municipal/provincial y hitos de la partida):

| Empresa | Ingresos que se le atribuyen | Desbloqueo sugerido |
|---|---|---|
| Agropecuaria (la granja) | Cultivos, productos animales | Desde el inicio |
| Minera | Minerales, gemas, lingotes | Acceso a la mina de Calico |
| Pesquera | Pescado | Nivel de pesca 6 + capital |
| Industrial / Artesanal | Productos de barriles, conservas, queso | X barriles + capital |
| Comercializadora / Exportadora | Comercio con Gotoro (mejor precio, más retenciones) | Año 2 |

- Cada empresa tiene su propia contabilidad, CUIT, régimen fiscal y calificación.
- Meta de final de juego: **IPO** de una empresa del jugador en BYMZ (pasa a cotizar; el jugador decide qué % vender).

---

## 9. Sistema tributario

### 9.1 Motor
Reglas en JSON: `{ impuesto, jurisdicción, hecho imponible, base, alícuota(s), régimen, período, vencimiento }`.
Los impuestos se **devengan** con cada operación y se **liquidan** en DDJJ al cierre de cada período.
La ARF/ARPV/Municipalidad envían la boleta por **correo**; se paga en la terminal o en la Municipalidad.

### 9.2 Impuestos (valores de referencia del nivel *Realista*)

| Jurisdicción | Impuesto | Base | Alícuota ref. | Período |
|---|---|---|---|---|
| Nacional | **Monotributo** | Cuota fija por categoría de facturación | Escala | Estación |
| Nacional | **IVA** | Débito (ventas, IVA incluido) − Crédito (compras) | 21% / 10,5% agro | Estación |
| Nacional | **Ganancias** (sociedades / personas) | Resultado impositivo | 25%–35% escalonada | Anual + anticipos |
| Nacional | **Bienes Personales** | Patrimonio del jugador al cierre | 0,5%–1,5% | Anual |
| Nacional | **Débitos y Créditos** ("impuesto al cheque") | Movimientos de la cuenta bancaria/comitente | 0,6% + 0,6% | Por operación |
| Nacional | **Derechos de exportación** (retenciones) | Valor FOB de commodities exportados | 0%–33% según producto | Por operación |
| Nacional | **Derechos de importación** (aranceles) + tasa estadística | Valor de ítems importados | 0%–35% + 3% | Por operación |
| Nacional | **Retenciones en la fuente** | Dividendos, cupones, intereses | 7%–15% | Por operación |
| Provincial | **Ingresos Brutos** | Facturación bruta | 1%–5% según actividad | Estación |
| Provincial | **Inmobiliario Rural** | Valuación fiscal de la granja | Escala | Anual (en cuotas) |
| Provincial | **Sellos** | Contratos (constitución de sociedades, préstamos) | 1%–1,2% | Por operación |
| Municipal | **Tasa de Seguridad e Higiene** | Facturación de cada empresa | 0,5%–1% | Estación |
| Municipal | **ABL / Tasa de servicios** | Edificios de la granja (por tipo y tamaño) | Monto fijo indexado por IPC | Estación |

- **Retenciones y percepciones** sufridas son **pago a cuenta** del impuesto final (crédito fiscal).
- Atraso: **intereses resarcitorios** diarios + multas → **intimación** → **embargo** (ver §11).
- Eventos fiscales: reformas, moratorias con plan de pagos, blanqueos, nuevas tasas municipales (votadas en el
  festival…).

---

## 10. Mercado de capitales

### 10.1 Infraestructura
- **Cuenta comitente** en un **ALyC** (agente de bolsa) ficticio; se transfiere oro desde/hacia la Caja.
- **Market maker** simulado con spread bid/ask y slippage según volumen; órdenes **de mercado** y **límite**
  (las límite se ejecutan en los ticks intradiarios o al cierre).
- **Comisiones** del ALyC + **derechos de mercado** + IVA sobre comisiones.
- **Liquidación**: CI (contado inmediato, T+0) y 24 hs (T+1).
- Curva de rendimientos soberana construida desde la tasa de política + prima por plazo + riesgo país.

### 10.2 Renta fija

| Instrumento | Descripción | Riesgos |
|---|---|---|
| **Caución** | Préstamo garantizado a 1–30 días. Colocadora (presta caja) o tomadora (se financia dejando títulos en garantía) | Tasa; para la tomadora, garantía insuficiente |
| **Letras** (LEFE — Letras del Tesoro de Ferngill) | Cupón cero, a descuento, 1–4 estaciones. Licitación cada estación | Tasa, inflación |
| **Bonos soberanos** | Cupón fijo, a tasa variable o **ajustables por CER-F**, con amortización. Ley local y ley Gotoro | Tasa, inflación, **default/reestructuración** |
| **Obligaciones Negociables (ON)** | Deuda de empresas ficticias y, más adelante, del jugador (requiere S.A. + calificación) | Crediticio según calificación |

Valuación: flujos descontados a `curva soberana + spread por calificación`. Probabilidad de default por calificación;
en default: cupones suspendidos, precio cae al valor de recupero esperado, posible canje.

### 10.3 Renta variable y derivados

| Instrumento | Descripción |
|---|---|
| **Acciones** | Empresas ficticias (y del jugador post-IPO). Dividendos con retención. Índice MERZU |
| **Futuros** (MATZU) | Sobre commodities, con vencimiento a fin de cada estación. **Entrega física**. Garantías y diferencias diarias |
| **Opciones** | Calls y puts europeas sobre acciones (entregan acciones) y sobre futuros de commodities. Valuación Black-Scholes / Black-76 con volatilidad histórica + sonrisa simple |

### 10.4 Futuros con entrega física
- Contrato: commodity, tamaño, calidad mínima (normal; plata/oro/iridio pagan prima), mes de entrega.
- **Garantía inicial** al abrir posición; **diferencias diarias** (mark-to-market). Si la garantía cae debajo del
  mínimo → **margin call**: hay hasta el próximo cierre para reponerla o se liquida la posición forzosamente.
- **Vencimiento**:
  - **Vendedor (short)**: debe depositar la mercadería en un **Silo de Acopio MATZU** (cofre especial en la granja
    y en el edificio de la bolsa) antes del último día de la estación. Cobra el precio pactado (ajustado por calidad).
  - **Comprador (long)**: paga y recibe la mercadería por correo / en el silo.
  - **Incumplimiento**: multa (% del valor) + liquidación al precio punitorio + baja de calificación del jugador.
- Esto permite **cubrir la cosecha**: plantar calabazas en otoño y venderlas a futuro al plantar.

### 10.5 Financiamiento del jugador
- Préstamos bancarios (Banco de Zuzu) con garantía de la granja; tasa según calificación del jugador.
- Toma de cauciones; emisión de ON (empresa S.A.); IPO.
- **Calificación crediticia del jugador** basada en patrimonio, endeudamiento, historial de pagos e incumplimientos.

---

## 11. Deudas, embargos y quiebra

Escalera de consecuencias (los plazos y la severidad dependen de la dificultad):

1. **Mora**: deuda vencida impaga → intereses punitorios diarios; aviso por correo.
2. **Intimación**: carta documento de la ARF/ARPV/Municipalidad/acreedor con plazo (p. ej., 7 días).
3. **Embargo**:
   - Embargo de **cuentas**: bloqueo de la cuenta comitente/bancaria por el monto adeudado.
   - Embargo de **ingresos**: un % de cada liquidación del cajón de envíos va directo al acreedor.
   - Embargo de **bienes muebles**: secuestro de ítems de los cofres (valuados con descuento) para rematar.
   - **Inhibición**: no se pueden abrir nuevas posiciones, pedir créditos ni constituir sociedades.
4. **Concurso preventivo** (lo puede pedir el jugador): congela intereses, propone un plan de pagos a los acreedores
   (aceptación según calificación y monto).
5. **Quiebra**: patrimonio neto negativo + incumplimiento del concurso o deuda impaga tras el embargo.
   - Liquidación de toda la cartera y de los ítems embargables.
   - Inhabilitación para operar en bolsa y ser socio de empresas por N estaciones.
   - Modo Argentina: se pueden rematar edificios de la granja (con confirmación explícita en la configuración).
   - **Nunca** se borra la partida; siempre existe un camino de recuperación.

---

## 12. Niveles de dificultad

| Parámetro | Casual | Realista | Argentina |
|---|---|---|---|
| Inflación mensual (por estación) | 0,5% | 2–4% | 5–25% con picos |
| Volatilidad de precios | Baja | Media | Alta |
| Frecuencia de eventos negativos | Baja | Media | Alta |
| Presión tributaria | ~50% de las alícuotas de referencia | 100% | 120% + tasas nuevas por eventos |
| Retenciones / aranceles | Bajas | Referencia | Altas y cambiantes |
| Interés por mora | Bajo | Referencia | Alto |
| Embargos | Solo de ingresos | Cuentas + ingresos + bienes | Todo + remate de edificios (opcional) |
| Quiebra | Desactivada (solo inhibición) | Activa | Activa y más rápida |
| Default soberano | Nunca | Raro | Probable |
| Tipo de cambio / cepo | No | No | Sí (fase tardía) |
| Moratorias y blanqueos | Frecuentes | Ocasionales | Frecuentes 😄 |

Cada parámetro puede además ajustarse individualmente en `config.json` (y con Generic Mod Config Menu si está instalado).

---

## 13. Interfaz

### 13.1 Acceso
- **Agencia Bursátil de Pelican Town**: edificio nuevo en el pueblo (mapa editado). Horario de atención: días hábiles
  9:00–17:00. Allí también se pagan impuestos y está el Silo de Acopio.
  *(Interino hasta tener el edificio: una acción sobre un mostrador/tile existente).*
- **Terminal Bursátil** (gran objeto fabricable): se desbloquea por carta del ALyC tras abrir la cuenta comitente.
  Permite operar desde la granja.
- **Tecla rápida** configurable (solo en modo debug o Casual).

### 13.2 Pantallas de la terminal
1. **Panel**: MERZU, tasa BCRF, inflación, noticias, alertas (margin calls, vencimientos, intimaciones).
2. **Cotizaciones**: tablas por tipo (commodities, acciones, bonos, letras, futuros, opciones).
3. **Gráfico**: velas/línea con historial diario e intradiario.
4. **Boleto de orden**: compra/venta, mercado/límite, plazo de liquidación, costos estimados (comisión + impuestos).
5. **Cartera**: tenencias, resultado, garantías, posiciones en futuros/opciones, cronograma de cupones y vencimientos.
6. **Impuestos**: deuda por jurisdicción, próximos vencimientos, DDJJ, planes de pago.
7. **Empresas**: contabilidad de cada empresa del jugador, estado de resultados, balance, constituir empresa.
8. **Legal**: deudas, embargos, concurso.

---

## 14. Hoja de ruta

| Fase | Contenido | Resultado jugable | Criterio de "listo" |
|---|---|---|---|
| **0 — Fundaciones** | Solución .NET, proyectos Core/Game/Tests, calendario económico, RNG determinista, Ledger, carga de datos JSON, persistencia, comandos de debug | El mod carga y guarda estado | Tests de Ledger y calendario en verde; el mod inicia en SMAPI sin errores |
| **1 — Commodities** | Modelo de precios, oferta por ventas, estacionalidad, inflación básica, eventos y noticias (correo/TV), parche de precios de venta | Los precios fluctúan y reaccionan a tus ventas | Vender en masa baja el precio; eventos visibles |
| **2 — Granja empresa + impuestos** | Contabilidad de la granja, Monotributo → Responsable Inscripto, IVA, IIBB, Seguridad e Higiene, ABL, Ganancias; boletas por correo; mora e intimaciones | Pagás impuestos cada estación | DDJJ correctas en tests; boletas en el juego |
| **3 — Bolsa I** | Cuenta comitente, empresas ficticias, acciones, MERZU, letras, cauciones, Terminal Bursátil + acceso interino en el pueblo | Invertís tu oro | Comprar/vender con costos e impuestos correctos |
| **4 — Renta fija completa** | BCRF y curva de tasas, bonos (fijos, variables, CER-F), ON, calificaciones, default | Cartera de renta fija | Valuación y cupones testeados |
| **5 — Derivados** | MATZU: futuros con garantías y entrega física, Silo de Acopio, opciones | Cubrís tu cosecha | Ciclo completo de un futuro con entrega |
| **6 — Legal** | Embargos, concurso, quiebra, préstamos bancarios, calificación del jugador | Las deudas tienen consecuencias | Escalera completa reproducible en tests |
| **7 — Expansión empresaria** | Nuevas empresas del jugador, S.A., emisión de ON propias, retenciones/aranceles completos, comercializadora Gotoro, IPO | Final del juego | IPO funcional |
| **8 — Edificio y pulido** | Agencia Bursátil en el pueblo, gráficos, balance de dificultades, i18n en/es, compatibilidad con GMCM | Versión 1.0 | Partida de prueba de 1 año en cada dificultad |
| **9 — Multijugador / Argentina avanzada** | Host autoritativo, cuentas por granjero; tipo de cambio, cepo, brecha | — | — |

---

## 15. Riesgos y mitigaciones

| Riesgo | Mitigación |
|---|---|
| Complejidad abrumadora para el jugador | Desbloqueo progresivo (Monotributo primero, derivados más tarde), tutoriales por carta, Casual |
| Economía rota (dinero infinito o imposible) | Simulaciones automáticas de 1–3 años en los tests con estrategias fijas; parámetros en JSON |
| Compatibilidad con otros mods que cambian precios | Parches Harmony mínimos y con prioridad configurable; API pública del mod para integraciones |
| Rendimiento (ticks intradiarios) | Simulación ligera, cálculo perezoso de precios, sin asignaciones por frame |
| Pruebas sin el juego en la nube | Toda la lógica en Core con tests; la integración se prueba localmente con logs de SMAPI |

---

## 16. Preguntas abiertas

- ¿Cuántos commodities en la primera versión? (propuesta: ~15 de los productos más comunes y luego expandir)
- ¿Nombres definitivos de las empresas ficticias y tickers?
- ¿Edificio nuevo en Pelican Town: ubicación? (propuesta: lote vacío al sur de la plaza)
- ¿La Agencia Bursátil tiene un NPC (agente de bolsa) con diálogos y amistad?
