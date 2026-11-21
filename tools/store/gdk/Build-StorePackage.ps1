param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^v?\d+\.\d+\.\d+$')]
    [string] $Version,

    [switch] $InstallForTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:OS -ne 'Windows_NT') {
    throw 'GDK packaging must run on Windows. Use a Windows machine with the .NET 10 SDK and Microsoft GDK installed.'
}

$makepkgCommand = Get-Command 'makepkg.exe' -ErrorAction SilentlyContinue
if ($null -eq $makepkgCommand) {
    throw 'makepkg.exe was not found. Run this script from the Microsoft GDK command prompt.'
}

$dotnetCommand = Get-Command 'dotnet' -ErrorAction SilentlyContinue
if ($null -eq $dotnetCommand) {
    throw '.NET 10 SDK was not found on PATH.'
}

$versionText = $Version.TrimStart('v')
if ($versionText -notmatch '^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)$') {
    throw 'Version must be a stable three-part version such as 0.2.0 or v0.2.0.'
}
$packageVersion = '{0}.{1}.{2}.0' -f $Matches.major, $Matches.minor, $Matches.patch

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$versionRoot = Join-Path $repoRoot "artifacts\store\gdk\$versionText"
if (Test-Path $versionRoot) {
    throw "Output already exists: $versionRoot. Move it aside or choose another version before rebuilding."
}

$publishRoot = Join-Path $versionRoot 'publish'
$contentDir = Join-Path $versionRoot 'content'
$assetsDir = Join-Path $contentDir 'Assets'
$packageDir = Join-Path $versionRoot 'package'
$layoutPath = Join-Path $versionRoot 'layout.xml'
$gameProject = Join-Path $repoRoot 'src\Emberward.Desktop\Emberward.Desktop.csproj'
$configTemplate = Join-Path $PSScriptRoot 'MicrosoftGame.config'
$iconPath = Join-Path $repoRoot 'src\Emberward.Browser\wwwroot\icons\icon-512.png'

New-Item -ItemType Directory -Force $versionRoot, $contentDir, $assetsDir, $packageDir | Out-Null

$targets = @(
    @{ Runtime = 'win-x64'; Architecture = 'x64'; PackageExecutable = 'Emberward.Desktop.exe' },
    @{ Runtime = 'win-arm64'; Architecture = 'ARM64'; PackageExecutable = 'Emberward.Desktop-arm64.exe' }
)

foreach ($target in $targets) {
    $targetPublishDir = Join-Path $publishRoot $target.Runtime
    Write-Host "Publishing Emberflow $versionText for Windows $($target.Architecture)..."
    & $dotnetCommand.Source publish $gameProject `
        -c Release `
        -r $target.Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=none `
        -o $targetPublishDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish for $($target.Runtime) failed with exit code $LASTEXITCODE."
    }

    $publishedFiles = @(Get-ChildItem -LiteralPath $targetPublishDir -File)
    if ($publishedFiles.Count -ne 1 -or $publishedFiles[0].Name -ne 'Emberward.Desktop.exe') {
        throw "Expected a single Emberward.Desktop.exe publish output for $($target.Runtime); found: $($publishedFiles.Name -join ', ')."
    }
    Copy-Item -LiteralPath $publishedFiles[0].FullName -Destination (Join-Path $contentDir $target.PackageExecutable)
}

[xml] $gameConfig = Get-Content -LiteralPath $configTemplate -Raw
$gameConfig.Game.Identity.SetAttribute('Version', $packageVersion)
$gameConfig.Save((Join-Path $contentDir 'MicrosoftGame.config'))

Add-Type -AssemblyName System.Drawing
$sourceIcon = [System.Drawing.Image]::FromFile($iconPath)
try {
    foreach ($asset in @(
        @{ Name = 'StoreLogo.png'; Size = 100 },
        @{ Name = 'Logo150.png'; Size = 150 },
        @{ Name = 'Logo44.png'; Size = 44 },
        @{ Name = 'Logo480.png'; Size = 480 }
    )) {
        $bitmap = [System.Drawing.Bitmap]::new($asset.Size, $asset.Size)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.DrawImage($sourceIcon, 0, 0, $asset.Size, $asset.Size)
            $bitmap.Save((Join-Path $assetsDir $asset.Name), [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $graphics.Dispose()
            $bitmap.Dispose()
        }
    }

    $splash = [System.Drawing.Bitmap]::new(1920, 1080)
    $graphics = [System.Drawing.Graphics]::FromImage($splash)
    try {
        $graphics.Clear([System.Drawing.Color]::FromArgb(9, 21, 27))
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.DrawImage($sourceIcon, 704, 284, 512, 512)
        $splash.Save((Join-Path $assetsDir 'SplashScreen.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $splash.Dispose()
    }
}
finally {
    $sourceIcon.Dispose()
}

Write-Host 'Generating the GDK package layout...'
& $makepkgCommand.Source genmap /f $layoutPath /d $contentDir
if ($LASTEXITCODE -ne 0) {
    throw "makepkg genmap failed with exit code $LASTEXITCODE."
}

Write-Host 'Packaging and running the GDK Submission Validator...'
& $makepkgCommand.Source pack /msixvc2 /f $layoutPath /d $contentDir /pc /pd $packageDir /validationcritical /updatesubval
if ($LASTEXITCODE -ne 0) {
    throw "makepkg pack failed with exit code $LASTEXITCODE."
}

$package = Get-ChildItem -LiteralPath $packageDir -Filter '*.msixvc' -File | Select-Object -First 1
if ($null -eq $package) {
    throw "GDK packaging reported success but no .msixvc package was written to $packageDir."
}

Write-Host "Created package: $($package.FullName)"
Write-Host "Review the Submission Validator log and install this package on Windows before uploading it to Partner Center."

if ($InstallForTest) {
    $wdappCommand = Get-Command 'wdapp.exe' -ErrorAction SilentlyContinue
    if ($null -eq $wdappCommand) {
        throw 'wdapp.exe was not found. Run this script from the Microsoft GDK command prompt.'
    }
    & $wdappCommand.Source install $package.FullName
    if ($LASTEXITCODE -ne 0) {
        throw "wdapp install failed with exit code $LASTEXITCODE."
    }
}
