param(
    [string]$OutPath,
    [switch]$CropTop,
    [int]$CropHeight = 60,
    [int]$RegionX = -1,
    [int]$RegionY = 0,
    [int]$RegionW = -1,
    [int]$RegionH = -1
)

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$screenBounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds

if ($RegionX -ge 0) {
    $bounds = New-Object System.Drawing.Rectangle($RegionX, $RegionY, $RegionW, $RegionH)
} elseif ($CropTop) {
    $bounds = New-Object System.Drawing.Rectangle(0, 0, $screenBounds.Width, $CropHeight)
} else {
    $bounds = $screenBounds
}

$bmp = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
$graphics = [System.Drawing.Graphics]::FromImage($bmp)
$graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
$bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose()
$bmp.Dispose()
Write-Output "saved $OutPath"
