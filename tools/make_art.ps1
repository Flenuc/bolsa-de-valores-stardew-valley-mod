param([string]$Out, [string]$Preview = "")
# Arte del mod en pixel art dibujado a mano: cada sprite es un mapa de caracteres con una paleta.
# Uso: tools\make_art.ps1 -Out src\ZuzuMarket.Game\assets [-Preview carpeta]   (Preview: copias ampliadas ×8)
Add-Type -AssemblyName System.Drawing

function C([string]$hex) { return [System.Drawing.ColorTranslator]::FromHtml($hex) }

$Palette = New-Object 'System.Collections.Generic.Dictionary[string,System.Drawing.Color]' ([StringComparer]::Ordinal)
$Palette['O'] = C '#2B1D1A'
$Palette['H'] = C '#5A3622'
$Palette['h'] = C '#7A4A2E'
$Palette['d'] = C '#3D2415'
$Palette['S'] = C '#F1C79D'
$Palette['s'] = C '#D9A07A'
$Palette['e'] = C '#B9805C'
$Palette['E'] = C '#26303F'
$Palette['T'] = C '#D7E6EF'
$Palette['G'] = C '#3A3A46'
$Palette['J'] = C '#2F3E66'
$Palette['j'] = C '#222D4D'
$Palette['k'] = C '#4A5E94'
$Palette['W'] = C '#EEF0F2'
$Palette['w'] = C '#C8CCD4'
$Palette['R'] = C '#B3262E'
$Palette['r'] = C '#7E1A20'
$Palette['P'] = C '#283152'
$Palette['p'] = C '#1C223B'
$Palette['B'] = C '#3A2416'
$Palette['M'] = C '#8C3B32'

function New-Canvas([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.Clear([System.Drawing.Color]::Transparent); $g.Dispose()
    return $bmp
}

# Dibuja un mapa (lista de filas) en (ox, oy); '.' es transparente. Las filas se completan o recortan al ancho.
function Draw-Map($bmp, [string[]]$rows, [int]$ox, [int]$oy, [int]$width, [switch]$Mirror) {
    for ($y = 0; $y -lt $rows.Count; $y++) {
        $row = $rows[$y].PadRight($width, '.').Substring(0, $width)
        for ($x = 0; $x -lt $width; $x++) {
            $ch = [string]$row[$x]
            if ($ch -ne '.' -and $Palette.ContainsKey($ch)) {
                $px = if ($Mirror) { $ox + $width - 1 - $x } else { $ox + $x }
                $bmp.SetPixel($px, $oy + $y, $Palette[$ch])
            }
        }
    }
}

function Save-Png($bmp, [string]$name) {
    $bmp.Save((Join-Path $Out $name), [System.Drawing.Imaging.ImageFormat]::Png)
    if ($Preview) {
        $big = New-Object System.Drawing.Bitmap ($bmp.Width * 8), ($bmp.Height * 8)
        $g = [System.Drawing.Graphics]::FromImage($big)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
        $g.Clear((C '#E9DDC0'))
        $g.DrawImage($bmp, 0, 0, $big.Width, $big.Height)
        $g.Dispose()
        $big.Save((Join-Path $Preview ("preview_" + $name)), [System.Drawing.Imaging.ImageFormat]::Png)
    }
}

# ───────────────────────────── Julián: sprite 16x32 (filas: abajo, derecha, arriba, izquierda) ─────────────────────────────
$front = @(
'................',
'................',
'....OOOOOOOO....',
'...OHHhhhHHHO...',
'..OHHhHHHHHHHO..',
'..OHHHHHHHHHdO..',
'..OHdSSSSSSHdO..',
'..OHSSSSSSSSHO..',
'..OSSSSSSSSSSO..',
'..OsGGGSSGGGsO..',
'..OsTETSSTETsO..',
'..OSSSSssSSSSO..',
'...OSSSSSSSSO...',
'...OSSSMMSSSO...',
'....OsSSSSsO....',
'....OOsSSsOO....',
'...OJJWRRWJJO...',
'..OJJJWRrWJJJO..',
'.OJJjJWRrWJjJJO.',
'.OJJjJJRrJJjJJO.',
'.OJJjJJRrJJjJJO.',
'.OJJjJJJJJJjJJO.',
'.OJJjJJkkJJjJJO.',
'.OJJjJJJJJJjJJO.',
'.OSOjJJJJJJjOSO.',
'.OeOPPPPPPPPOeO.',
'..OOPPPPPPPPOO..',
'...OPPPppPPPO...',
'...OPPpOOpPPO...',
'...OPPpOOpPPO...',
'...OBBBOOBBBO...',
'...OOOOOOOOOO...'
)
$side = @(
'................',
'................',
'.....OOOOOO.....',
'....OHHhhHHO....',
'...OHHhHHHHHO...',
'...OHHHHHHHHO...',
'...OHHHHSSSSO...',
'...OHHHSSSSSSO..',
'...OHHSSSSSSSO..',
'...OHHSSSGGGGO..',
'...OHeSSSTETSO..',
'....OSSSSSSSsO..',
'....OSSSSSSSO...',
'....OsSSSSMO....',
'.....OsSSsOO....',
'.....OOsSSO.....',
'....OJJJWRO.....',
'...OJJJJWRO.....',
'...OJJjJJRO.....',
'...OJJjJJJO.....',
'...OJJjJJJO.....',
'...OJJjJJJO.....',
'...OJkjJJJO.....',
'...OJJjJJJO.....',
'...OJSOJJJO.....',
'...OOePPPPO.....',
'....OPPPPPO.....',
'....OPPpPPO.....',
'....OPPOPPO.....',
'....OPPOPPO.....',
'....OBBOBBBO....',
'....OOOOOOOO....'
)
$back = @(
'................',
'................',
'....OOOOOOOO....',
'...OHHhhhHHHO...',
'..OHHhHHHHHHHO..',
'..OHHHHHHHHHdO..',
'..OHHHHhHHHHdO..',
'..OHHHHHHHHHdO..',
'..OdHHHHHHHHdO..',
'..OdHHHHHHHHdO..',
'..OSdHHHHHHdSO..',
'..OSddHHHHddSO..',
'...OddddddddO...',
'...OSSSSSSSSO...',
'....OsSSSSsO....',
'....OOsSSsOO....',
'...OJJJJJJJJO...',
'..OJJJJJJJJJJO..',
'.OJJjJJJJJJjJJO.',
'.OJJjJJJJJJjJJO.',
'.OJJjJJJJJJjJJO.',
'.OJJjJJjjJJjJJO.',
'.OJJjJJjjJJjJJO.',
'.OJJjJJJJJJjJJO.',
'.OSOjJJJJJJjOSO.',
'.OeOPPPPPPPPOeO.',
'..OOPPPPPPPPOO..',
'...OPPPppPPPO...',
'...OPPpOOpPPO...',
'...OPPpOOpPPO...',
'...OBBBOOBBBO...',
'...OOOOOOOOOO...'
)

# Pasos: en los cuadros 1 y 3 el cuerpo sube un píxel y una pierna se adelanta.
function Walk([string[]]$base, [int]$frame, [bool]$sideView) {
    $rows = [System.Collections.Generic.List[string]]::new()
    $base | ForEach-Object { $rows.Add($_) }
    if ($frame -eq 0 -or $frame -eq 2) { return $rows.ToArray() }
    # Sube todo menos las piernas (filas 0-26 suben una fila).
    $up = [System.Collections.Generic.List[string]]::new()
    for ($i = 1; $i -le 26; $i++) { $up.Add($rows[$i]) }
    $up.Add($rows[26])
    if ($sideView) {
        $legs = if ($frame -eq 1) { @('....OPPpPPO.....', '...OPPO.OPPO....', '...OPPO..OPO....', '..OBBBO..OBBO...', '..OOOO...OOOO...') }
                else { @('....OPPpPPO.....', '....OPPOPPO.....', '....OPOOPPO.....', '....OBOOBBBO....', '....OOOOOOOO....') }
    }
    else {
        $legs = if ($frame -eq 1) { @('...OPPPppPPPO...', '...OPPpOOpPPO...', '...OBBBOOpPPO...', '...OOOOOOBBBO...', '.........OOOO...') }
                else { @('...OPPPppPPPO...', '...OPPpOOpPPO...', '...OPPpOOBBBO...', '...OBBBOOOOOO...', '...OOOO.........') }
    }
    $legs | ForEach-Object { $up.Add($_) }
    return $up.ToArray()
}

$sprite = New-Canvas 64 128
for ($f = 0; $f -lt 4; $f++) {
    Draw-Map $sprite (Walk $front $f $false) ($f * 16) 0 16
    Draw-Map $sprite (Walk $side $f $true) ($f * 16) 32 16
    Draw-Map $sprite (Walk $back $f $false) ($f * 16) 64 16
    Draw-Map $sprite (Walk $side $f $true) ($f * 16) 96 16 -Mirror
}
Save-Png $sprite 'julian.png'

# ───────────────────────────── Julián: retrato 32x32 ampliado ×2 (neutral, contento, triste, sorprendido) ─────────────────────────────
$portraitBase = @(
'................................',
'...........OOOOOOOOO............',
'.........OOHHHhhhHHHOO..........',
'........OHHHhhhhHHHHHHO.........',
'.......OHHHhhHHHHHHHHHHO........',
'.......OHHhHHHHHHHHHHHHdO.......',
'......OHHHHHHHHHHHHHHHHdO.......',
'......OHHHHHHHHHHHHHHHddO.......',
'......OHHdSSSSSSSSSSSHddO.......',
'......OHdSSSSSSSSSSSSSHdO.......',
'......OHdSSSSSSSSSSSSSSdO.......',
'......OdSSdddSSSSSdddSSSO.......',
'.....OsSSSSSSSSSSSSSSSSSsO......',
'.....OsGGGGGGSSSSGGGGGGSsO......',
'.....OsGTTETGGGGGGTTETGSsO......',
'.....OeGTTTTGSSSSGTTTTGSeO......',
'.....OsGGGGGGSSsSGGGGGGSsO......',
'......OSSSSSSSSsSSSSSSSSO.......',
'......OSSSSSSSsssSSSSSSSO.......',
'......OsSSSSSSSSSSSSSSSsO.......',
'.......OSSSSSMMMMSSSSSSO........',
'.......OsSSSSSSSSSSSSSsO........',
'........OsSSSSSSSSSSSsO.........',
'.........OOsSSSSSSSsOO..........',
'...........OsSSSSSsO............',
'......OOOOOJWWRRWWJOOOOO........',
'....OOJJJJJJWWRRWWJJJJJJOO......',
'...OJJJJJJJJjWRRWjJJJJJJJJO.....',
'..OJJJJJJJJJjWRrWjJJJJJJJJJO....',
'.OJJJJkJJJJJjWRrWjJJJJJkJJJJO...',
'.OJJJJkJJJJJJjRrjJJJJJJkJJJJO...',
'.OJJJJkJJJJJJJjjJJJJJJJkJJJJJO..'
)
# Cada expresión cambia algunas filas: índice de fila → fila nueva.
$expressions = @(
    @{},
    @{ 20 = '.......OSSSMSSSSSSMSSSSO........'; 21 = '.......OsSSSMMMMMMSSSsO.........' },
    @{ 11 = '......OdSSSSddSSSddSSSSSO.......'; 20 = '.......OSSSSSSSSSSSSSSSO........'; 21 = '.......OsSSSMMMMMMSSSsO.........'; 22 = '........OsSMSSSSSSMSsO..........' },
    @{ 10 = '......OHdSSdddSSSSSdddSdO.......'; 11 = '......OdSSSSSSSSSSSSSSSSO.......'; 14 = '.....OsGTEETGGGGGGTEETGSsO......'; 20 = '.......OSSSSSSMMSSSSSSO.........'; 21 = '.......OsSSSSSMMSSSSSsO.........' }
)
$portrait = New-Canvas 128 128
for ($i = 0; $i -lt 4; $i++) {
    $rows = [string[]]$portraitBase.Clone()
    foreach ($k in $expressions[$i].Keys) { $rows[[int]$k] = $expressions[$i][$k] }
    $small = New-Canvas 32 32
    Draw-Map $small $rows 0 0 32
    $g = [System.Drawing.Graphics]::FromImage($portrait)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    $g.DrawImage($small, ($i % 2) * 64, [Math]::Floor($i / 2) * 64, 64, 64)
    $g.Dispose()
}
Save-Png $portrait 'julian_portrait.png'

# ───────────────────────────── Fachada 48x64 (techo + 3 filas) ─────────────────────────────
$Palette['A'] = C '#7A2E2E'; $Palette['a'] = C '#5C2020'; $Palette['q'] = C '#9A4438'   # tejas
$Palette['C'] = C '#E8D9B5'; $Palette['c'] = C '#CBB68C'; $Palette['x'] = C '#A8916A'   # pared
$Palette['N'] = C '#6B4426'; $Palette['n'] = C '#4E2F19'; $Palette['m'] = C '#8A5A35'   # madera
$Palette['V'] = C '#8FB8D6'; $Palette['v'] = C '#5E87A6'; $Palette['Y'] = C '#E8C25A'   # vidrio, oro
$Palette['F'] = C '#1F4E3D'; $Palette['f'] = C '#2F6B54'; $Palette['g'] = C '#7BD389'   # cartel, gráfico
$Palette['Z'] = C '#8C8C96'; $Palette['z'] = C '#6E6E78'                                # piedra
$facade = @(
'................................................',
'................................................',
'.....OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO.....',
'....OAqAAqAAqAAqAAqAAqAAqAAqAAqAAqAAqAAqAAAO....',
'...OAAaAAaAAaAAaAAaAAaAAaAAaAAaAAaAAaAAaAAAAO...',
'..OqAAqAAqAAqAAqAAqAAqAAqAAqAAqAAqAAqAAqAAqAAO..',
'.OAAaAAaAAaAAaAAaAAaAAaAAaAAaAAaAAaAAaAAaAAaAAO.',
'OAqAAqAAqAAqAAqAAqAAqAAqAAqAAqAAqAAqAAqAAqAAqAAO',
'OAAaAAaAAaAAaAAaAAaAAaAAaAAaAAaAAaAAaAAaAAaAAaAO',
'OqAAqAAqAAqAAqAAqAAqAAqAAqAAqAAqAAqAAqAAqAAqAAqO',
'OAaAAaAAaAAaAAaAAaAAaAAaAAaAAaAAaAAaAAaAAaAAaAAO',
'OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO',
'.OnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnO.',
'.ONmNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNNmNO.',
'.ONnCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCnNO.',
'.ONnCOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOCnNO.',
'.ONnCOFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFOCnNO.',
'.ONnCOFFFYYYYFFFFFFFFFFFFFFFFFFFFFFFFFgggFOCnNO.',
'.ONnCOFFYYOOYYFFFFFFFFFFFFFFFFFFFFFFggFFgFOCnNO.',
'.ONnCOFFYOYYOYFFFFFFFFFFFFFFFFFgggggFFFFFFOCnNO.',
'.ONnCOFFYYOOYYFFFFFFFFFgggggggFFFFFFFFFFFFOCnNO.',
'.ONnCOFFFYYYYFFFFgggggFFFFFFFFFFFFFFFFFFFFOCnNO.',
'.ONnCOFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFOCnNO.',
'.ONnCOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOCnNO.',
'.ONnCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCnNO.',
'.ONnCcCcCcCcCcCcCcCcCcCcCcCcCcCcCcCcCcCcCcCcnNO.',
'.ONnCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCnNO.',
'.ONnCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCnNO.',
'.ONnCcCcCcCcCcCcCcCcCcCcCcCcCcCcCcCcCcCcCcCcnNO.',
'.ONnCNNNNNNNNNNCCCCCCCCCCCCCCCCCCNNNNNNNNNNCnNO.',
'.ONnCNOOOOOOOONCCCCCCCCCCCCCCCCCCNOOOOOOOONCnNO.',
'.ONnCNOVVVOVVONCCCCOOOOOOOOOOCCCCNOVVVOVVONCnNO.',
'.ONnCNOVTVOVTONCCCOnnnnnnnnnnOCCCNOVTVOVTONCnNO.',
'.ONnCNOVVVOVVONCCCOnNNNNNNNNnOCCCNOVVVOVVONCnNO.',
'.ONnCNOVVvOVVvNCCCOnNmNNNNmNnOCCCNOVVvOVVvNCnNO.',
'.ONnCNOOOOOOOONCCCOnNNNNNNNNnOCCCNOOOOOOOONCnNO.',
'.ONnCNOVVVOVVONCCCOnNmNNNNmNnOCCCNOVVVOVVONCnNO.',
'.ONnCNOVTVOVTONCCCOnNNNNNNNNnOCCCNOVTVOVTONCnNO.',
'.ONnCNOVVvOVVvNCCCOnNmNNNNmNnOCCCNOVVvOVVvNCnNO.',
'.ONnCNOOOOOOOONCCCOnNNNNNNNNnOCCCNOOOOOOOONCnNO.',
'.ONnCNNNNNNNNNNCCCOnNmNNNNmNnOCCCNNNNNNNNNNCnNO.',
'.ONnCxxxxxxxxxxCCCOnNNNNNYNNnOCCCxxxxxxxxxxCnNO.',
'.ONnCCCCCCCCCCCCCCOnNNNNNYNNnOCCCCCCCCCCCCCCnNO.',
'.ONnCcCcCcCcCcCcCcOnNmNNNNmNnOcCcCcCcCcCcCcCnNO.',
'.ONnCCCCCCCCCCCCCCOnNNNNNNNNnOCCCCCCCCCCCCCCnNO.',
'.ONnCCCCCCCCCCCCCCOnNmNNNNmNnOCCCCCCCCCCCCCCnNO.',
'.ONnCcCcCcCcCcCcCcOnNNNNNNNNnOcCcCcCcCcCcCcCnNO.',
'.ONnCCCCCCCCCCCCCCOnNmNNNNmNnOCCCCCCCCCCCCCCnNO.',
'.ONnCCCCCCCCCCCCCCOnNNNNNNNNnOCCCCCCCCCCCCCCnNO.',
'.ONnCcCcCcCcCcCcCcOnNmNNNNmNnOcCcCcCcCcCcCcCnNO.',
'.ONnCCCCCCCCCCCCCCOnNNNNNNNNnOCCCCCCCCCCCCCCnNO.',
'.ONnCCCCCCCCCCCCCCOnNmNNNNmNnOCCCCCCCCCCCCCCnNO.',
'.ONnCcCcCcCcCcCcCcOnNNNNNNNNnOcCcCcCcCcCcCcCnNO.',
'.ONnCCCCCCCCCCCCCCOnNmNNNNmNnOCCCCCCCCCCCCCCnNO.',
'.ONnCCCCCCCCCCCCCCOnNNNNNNNNnOCCCCCCCCCCCCCCnNO.',
'.ONnCcCcCcCcCcCcCcOnNmNNNNmNnOcCcCcCcCcCcCcCnNO.',
'.ONnZZZZZZZZZZZZZZOnNNNNNNNNnOZZZZZZZZZZZZZZnNO.',
'.ONnZzZZzZZzZZzZZzOnNmNNNNmNnOzZZzZZzZZzZZzZnNO.',
'.ONnzZZzZZzZZzZZzZOnNNNNNNNNnOZzZZzZZzZZzZZznNO.',
'.ONnZZZZZZZZZZZZZZOnNNNNNNNNnOZZZZZZZZZZZZZZnNO.',
'.ONnZzZZzZZzZZzZZzOnNmNNNNmNnOzZZzZZzZZzZZzZnNO.',
'.ONnzZZzZZzZZzZZzZOOOOOOOOOOOOZzZZzZZzZZzZZznNO.',
'.ONnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnNO.',
'.OOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOOO.'
)
$ext = New-Canvas 48 64
Draw-Map $ext $facade 0 0 48
Save-Png $ext 'agency_exterior.png'

# ───────────────────────────── Billete gotoro 16x16 ─────────────────────────────
$Palette['K'] = C '#7DBF7A'; $Palette['L'] = C '#5FA25C'; $Palette['l'] = C '#3F7A3D'; $Palette['u'] = C '#1E4D2B'
$billMap = @(
'................',
'................',
'................',
'OOOOOOOOOOOOOO..',
'OllllllllllllO..',
'OlKKKKKKKKKKlOO.',
'OlKuKKLYYLKKlOlO',
'OlKKKLYuuYLKlOlO',
'OlKKKLYuuYLKlOlO',
'OlKKKKLYYLKuKlOO',
'OlKKKKKKKKKKKlO.',
'OllllllllllllOlO',
'OOOOOOOOOOOOOOlO',
'..OllllllllllllO',
'..OOOOOOOOOOOOOO',
'................'
)
$bill = New-Canvas 16 16
Draw-Map $bill $billMap 0 0 16
Save-Png $bill 'gotoro_bill.png'
"ok"
