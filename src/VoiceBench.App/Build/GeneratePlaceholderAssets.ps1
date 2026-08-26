param(
    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

[System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null

function New-VoiceBenchLogo {
    param(
        [Parameter(Mandatory = $true)] [string] $Path,
        [Parameter(Mandatory = $true)] [int] $Width,
        [Parameter(Mandatory = $true)] [int] $Height,
        [Parameter(Mandatory = $true)] [switch] $Wide
    )

    $bitmap = New-Object System.Drawing.Bitmap($Width, $Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([System.Drawing.ColorTranslator]::FromHtml('#0E1014'))

        $cyan = New-Object System.Drawing.Pen([System.Drawing.ColorTranslator]::FromHtml('#35C7E8'), [Math]::Max(2, $Width / 44))
        $textBrush = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#EDEFF3'))
        try {
            $iconSize = [Math]::Min($Height * 0.46, $Width * 0.46)
            $left = if ($Wide) { $Width * 0.14 } else { ($Width - $iconSize) / 2 }
            $top = ($Height - $iconSize) / 2
            $graphics.DrawRectangle($cyan, $left, $top, $iconSize, $iconSize)
            $midY = $Height / 2
            $graphics.DrawLine($cyan, $left + ($iconSize * 0.20), $midY, $left + ($iconSize * 0.40), $midY)
            $graphics.DrawLine($cyan, $left + ($iconSize * 0.40), $midY, $left + ($iconSize * 0.50), $midY - ($iconSize * 0.20))
            $graphics.DrawLine($cyan, $left + ($iconSize * 0.50), $midY - ($iconSize * 0.20), $left + ($iconSize * 0.61), $midY + ($iconSize * 0.20))
            $graphics.DrawLine($cyan, $left + ($iconSize * 0.61), $midY + ($iconSize * 0.20), $left + ($iconSize * 0.80), $midY)

            if ($Wide) {
                $font = New-Object System.Drawing.Font('Bahnschrift', [Math]::Max(12, $Height * 0.13), [System.Drawing.FontStyle]::Bold)
                try {
                    $graphics.DrawString('VOICEBENCH', $font, $textBrush, $left + $iconSize + ($Width * 0.05), $midY - ($font.Height / 2))
                }
                finally {
                    $font.Dispose()
                }
            }
        }
        finally {
            $cyan.Dispose()
            $textBrush.Dispose()
        }

        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

New-VoiceBenchLogo -Path (Join-Path $OutputDirectory 'Square44x44Logo.png') -Width 44 -Height 44 -Wide:$false
New-VoiceBenchLogo -Path (Join-Path $OutputDirectory 'Square150x150Logo.png') -Width 150 -Height 150 -Wide:$false
New-VoiceBenchLogo -Path (Join-Path $OutputDirectory 'Wide310x150Logo.png') -Width 310 -Height 150 -Wide
New-VoiceBenchLogo -Path (Join-Path $OutputDirectory 'SplashScreen.png') -Width 620 -Height 300 -Wide
