$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$eclipseRoot = Split-Path $PSScriptRoot -Parent
$eclipseIconPath = Join-Path $eclipseRoot 'src\Eclipse.Launcher\Assets\Eclipse.ico'
$eclipsePixels = @(
    '................', '......####......', '....########....', '...###....###...',
    '..###......###..', '..##........##..', '.###........###.', '.###........###.',
    '.###........###.', '..##........##..', '..###......###..', '...###....###...',
    '....########....', '......####......', '................', '................'
)
$eclipseSizes = @(16, 32, 48, 64, 128, 256)
$eclipseImages = [System.Collections.Generic.List[byte[]]]::new()
foreach ($eclipseSize in $eclipseSizes) {
    $eclipseBitmap = [System.Drawing.Bitmap]::new($eclipseSize, $eclipseSize)
    $eclipseGraphics = [System.Drawing.Graphics]::FromImage($eclipseBitmap)
    $eclipseGraphics.Clear([System.Drawing.Color]::Transparent)
    $eclipseInk = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(173,205,132))
    $eclipseDisk = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(7,9,8))
    $eclipseUnit = $eclipseSize / 16
    for ($eclipseY = 0; $eclipseY -lt 16; $eclipseY++) {
        for ($eclipseX = 0; $eclipseX -lt 16; $eclipseX++) {
            if ([Math]::Pow($eclipseX - 7.5, 2) + [Math]::Pow($eclipseY - 7, 2) -lt 35) {
                $eclipseGraphics.FillRectangle($eclipseDisk, [single]($eclipseX * $eclipseUnit), [single]($eclipseY * $eclipseUnit), [single]$eclipseUnit, [single]$eclipseUnit)
            }
            if ($eclipsePixels[$eclipseY][$eclipseX] -eq '#') {
                $eclipseGraphics.FillRectangle($eclipseInk, [single]($eclipseX * $eclipseUnit), [single]($eclipseY * $eclipseUnit), [single]$eclipseUnit, [single]$eclipseUnit)
            }
        }
    }
    $eclipseStream = [System.IO.MemoryStream]::new()
    $eclipseBitmap.Save($eclipseStream, [System.Drawing.Imaging.ImageFormat]::Png)
    $eclipseImages.Add($eclipseStream.ToArray())
    $eclipseStream.Dispose(); $eclipseGraphics.Dispose(); $eclipseInk.Dispose(); $eclipseDisk.Dispose(); $eclipseBitmap.Dispose()
}
$eclipseOutput = [System.IO.File]::Create($eclipseIconPath)
$eclipseWriter = [System.IO.BinaryWriter]::new($eclipseOutput)
try {
    $eclipseWriter.Write([uint16]0); $eclipseWriter.Write([uint16]1); $eclipseWriter.Write([uint16]$eclipseSizes.Count)
    $eclipseOffset = 6 + 16 * $eclipseSizes.Count
    for ($eclipseIndex = 0; $eclipseIndex -lt $eclipseSizes.Count; $eclipseIndex++) {
        $eclipseDimension = if ($eclipseSizes[$eclipseIndex] -eq 256) { 0 } else { $eclipseSizes[$eclipseIndex] }
        $eclipseWriter.Write([byte]$eclipseDimension); $eclipseWriter.Write([byte]$eclipseDimension)
        $eclipseWriter.Write([byte]0); $eclipseWriter.Write([byte]0)
        $eclipseWriter.Write([uint16]1); $eclipseWriter.Write([uint16]32)
        $eclipseWriter.Write([uint32]$eclipseImages[$eclipseIndex].Length); $eclipseWriter.Write([uint32]$eclipseOffset)
        $eclipseOffset += $eclipseImages[$eclipseIndex].Length
    }
    foreach ($eclipseImage in $eclipseImages) { $eclipseWriter.Write($eclipseImage) }
}
finally { $eclipseWriter.Dispose(); $eclipseOutput.Dispose() }
$eclipseValidation = [System.Drawing.Icon]::new($eclipseIconPath, 32, 32)
$eclipseValidation.Dispose()
Write-Output $eclipseIconPath
