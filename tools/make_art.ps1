param([string]$Out)
Add-Type -AssemblyName System.Drawing

function New-Canvas([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.Dispose()
    return $bmp
}
function C([string]$hex) { return [System.Drawing.ColorTranslator]::FromHtml($hex) }
function Rect($bmp, [int]$x, [int]$y, [int]$w, [int]$h, $color) {
    for ($i = $x; $i -lt $x + $w; $i++) { for ($j = $y; $j -lt $y + $h; $j++) {
        if ($i -ge 0 -and $j -ge 0 -and $i -lt $bmp.Width -and $j -lt $bmp.Height) { $bmp.SetPixel($i, $j, $color) } } }
}
function Px($bmp, [int]$x, [int]$y, $color) { if ($x -ge 0 -and $y -ge 0 -and $x -lt $bmp.Width -and $y -lt $bmp.Height) { $bmp.SetPixel($x, $y, $color) } }

$skin = C '#F2C29B'; $skinShade = C '#D9A07A'; $hair = C '#5A3A22'; $hairDark = C '#3E2716'
$suit = C '#2E3B5E'; $suitDark = C '#1F2842'; $shirt = C '#F4F4F4'; $tie = C '#B3262E'
$pants = C '#262E48'; $shoe = C '#2A1A10'; $eye = C '#1A1A1A'; $outline = C '#1B1B24'

# ───────────── Sprite 16x32, 4 columnas x 4 filas (abajo, derecha, arriba, izquierda) ─────────────
$sprite = New-Canvas 64 128
function Draw-Walker($bmp, [int]$ox, [int]$oy, [int]$dir, [int]$frame) {
    $bob = if ($frame -eq 1 -or $frame -eq 3) { 1 } else { 0 }
    $y0 = $oy + 6 + $bob
    # cabeza
    Rect $bmp ($ox+4) ($y0) 8 8 $skin
    Rect $bmp ($ox+4) ($y0) 8 3 $hair
    if ($dir -eq 2) { Rect $bmp ($ox+4) ($y0) 8 7 $hair }                     # de espaldas
    if ($dir -eq 1) { Rect $bmp ($ox+4) ($y0) 3 6 $hair; Px $bmp ($ox+10) ($y0+4) $eye }  # derecha
    if ($dir -eq 3) { Rect $bmp ($ox+9) ($y0) 3 6 $hair; Px $bmp ($ox+5) ($y0+4) $eye }   # izquierda
    if ($dir -eq 0) { Px $bmp ($ox+6) ($y0+4) $eye; Px $bmp ($ox+9) ($y0+4) $eye; Px $bmp ($ox+7) ($y0+6) $skinShade; Px $bmp ($ox+8) ($y0+6) $skinShade }
    # torso (saco)
    $ty = $y0 + 8
    Rect $bmp ($ox+3) $ty 10 9 $suit
    Rect $bmp ($ox+3) $ty 1 9 $suitDark; Rect $bmp ($ox+12) $ty 1 9 $suitDark
    if ($dir -eq 0) { Rect $bmp ($ox+7) $ty 2 4 $shirt; Rect $bmp ($ox+7) ($ty+1) 2 4 $tie }
    # brazos
    $swing = if ($frame -eq 1) { 1 } elseif ($frame -eq 3) { -1 } else { 0 }
    Rect $bmp ($ox+2) ($ty+1+$swing) 1 7 $suitDark; Rect $bmp ($ox+13) ($ty+1-$swing) 1 7 $suitDark
    Px $bmp ($ox+2) ($ty+8+$swing) $skin; Px $bmp ($ox+13) ($ty+8-$swing) $skin
    # piernas
    $ly = $ty + 9
    $l = if ($frame -eq 1) { 1 } else { 0 }; $r = if ($frame -eq 3) { 1 } else { 0 }
    Rect $bmp ($ox+4) $ly 3 (7-$l) $pants; Rect $bmp ($ox+9) $ly 3 (7-$r) $pants
    Rect $bmp ($ox+4) ($ly+7-$l) 3 1 $shoe; Rect $bmp ($ox+9) ($ly+7-$r) 3 1 $shoe
}
for ($row = 0; $row -lt 4; $row++) { for ($col = 0; $col -lt 4; $col++) { Draw-Walker $sprite ($col*16) ($row*32) $row $col } }
$sprite.Save((Join-Path $Out 'julian.png'), [System.Drawing.Imaging.ImageFormat]::Png)

# ───────────── Retrato 64x64 x 4 (neutral, contento, triste, sorprendido) ─────────────
$portrait = New-Canvas 128 128
$bg = C '#D8C7A0'
function Draw-Portrait($bmp, [int]$ox, [int]$oy, [int]$mood) {
    Rect $bmp $ox $oy 64 64 $bg
    Rect $bmp ($ox+10) ($oy+44) 44 20 $suit                     # hombros
    Rect $bmp ($ox+27) ($oy+44) 10 20 $shirt
    Rect $bmp ($ox+30) ($oy+46) 4 18 $tie
    Rect $bmp ($ox+26) ($oy+38) 12 8 $skinShade                 # cuello
    Rect $bmp ($ox+18) ($oy+12) 28 30 $skin                     # cara
    Rect $bmp ($ox+16) ($oy+6) 32 10 $hair                      # pelo
    Rect $bmp ($ox+16) ($oy+10) 4 14 $hair; Rect $bmp ($ox+44) ($oy+10) 4 12 $hair
    Rect $bmp ($ox+18) ($oy+6) 28 3 $hairDark
    # ojos y cejas
    $browY = if ($mood -eq 3) { 18 } elseif ($mood -eq 2) { 21 } else { 20 }
    Rect $bmp ($ox+22) ($oy+$browY) 6 1 $hairDark; Rect $bmp ($ox+36) ($oy+$browY) 6 1 $hairDark
    Rect $bmp ($ox+23) ($oy+24) 4 3 $eye; Rect $bmp ($ox+37) ($oy+24) 4 3 $eye
    if ($mood -eq 3) { Rect $bmp ($ox+23) ($oy+23) 4 5 $eye; Rect $bmp ($ox+37) ($oy+23) 4 5 $eye }
    Rect $bmp ($ox+31) ($oy+28) 2 5 $skinShade                  # nariz
    # boca
    switch ($mood) {
        0 { Rect $bmp ($ox+27) ($oy+35) 10 1 $outline }
        1 { Rect $bmp ($ox+26) ($oy+34) 12 1 $outline; Px $bmp ($ox+25) ($oy+33) $outline; Px $bmp ($ox+38) ($oy+33) $outline; Rect $bmp ($ox+27) ($oy+35) 10 1 $outline }
        2 { Rect $bmp ($ox+27) ($oy+35) 10 1 $outline; Px $bmp ($ox+26) ($oy+36) $outline; Px $bmp ($ox+37) ($oy+36) $outline }
        3 { Rect $bmp ($ox+29) ($oy+33) 6 4 $outline }
    }
    # anteojos finos (agente de bolsa)
    Rect $bmp ($ox+21) ($oy+22) 8 1 $outline; Rect $bmp ($ox+35) ($oy+22) 8 1 $outline
    Rect $bmp ($ox+21) ($oy+28) 8 1 $outline; Rect $bmp ($ox+35) ($oy+28) 8 1 $outline
    Rect $bmp ($ox+29) ($oy+24) 6 1 $outline
}
Draw-Portrait $portrait 0 0 0; Draw-Portrait $portrait 64 0 1; Draw-Portrait $portrait 0 64 2; Draw-Portrait $portrait 64 64 3
$portrait.Save((Join-Path $Out 'julian_portrait.png'), [System.Drawing.Imaging.ImageFormat]::Png)

# ───────────── Fachada: tilesheet 48x64 (3 tiles de ancho x 4 de alto: techo + 3 filas de frente) ─────────────
$ext = New-Canvas 48 64
$roof = C '#7A2E2E'; $roofDark = C '#5C2020'; $wall = C '#E3D3B0'; $wallDark = C '#C2AE87'; $wood = C '#6B4426'
$glass = C '#8FB8D6'; $glassDark = C '#5E87A6'; $sign = C '#1F4E3D'; $gold = C '#E8C25A'
Rect $ext 0 4 48 12 $roof; Rect $ext 0 14 48 2 $roofDark                          # techo (fila 0)
for ($i = 0; $i -lt 48; $i += 4) { Rect $ext $i 6 2 8 $roofDark }
Rect $ext 0 16 48 48 $wall; Rect $ext 0 16 2 48 $wallDark; Rect $ext 46 16 2 48 $wallDark
Rect $ext 4 18 40 10 $sign; Rect $ext 5 19 38 8 $sign                             # cartel (fila 1)
for ($i = 8; $i -lt 40; $i += 5) { Rect $ext $i 21 3 4 $gold }                    # "letras"
Rect $ext 3 34 11 12 $glassDark; Rect $ext 4 35 9 10 $glass                       # ventanas (fila 2)
Rect $ext 34 34 11 12 $glassDark; Rect $ext 35 35 9 10 $glass
Rect $ext 17 34 14 30 $wood; Rect $ext 18 35 12 29 (C '#7E5232')                  # puerta (filas 2-3)
Px $ext 27 50 $gold; Px $ext 27 51 $gold
Rect $ext 0 62 48 2 $wallDark
$ext.Save((Join-Path $Out 'agency_exterior.png'), [System.Drawing.Imaging.ImageFormat]::Png)
"ok"
