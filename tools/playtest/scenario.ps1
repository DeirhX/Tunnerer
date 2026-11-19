# Runs one deterministic playtest: scripted player input with the AI opponent on, in a hidden 1280x720 window,
# writing in-game final-frame screenshots (HUD included) at the given simulation frames (24 frames = 1 s).
#
#   .\tools\playtest\scenario.ps1 -Name duck -Script '0,1:72;0,0:3600' -Shots '24,72,120' -Seconds 60
#
# Script segments are "dx,dy[,shoot[,build]]:frames" separated by ';'. Output goes to artifacts\playtest\<Name>\
# (.ppm screenshots plus game.out/game.err). Build the Release configuration first.
param(
    [Parameter(Mandatory)][string]$Name,
    [Parameter(Mandatory)][string]$Script,
    [Parameter(Mandatory)][string]$Shots,
    [int]$Seconds = 45,
    [string]$Extra = '')

$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$exeDir = Join-Path $root 'src\Emberward.Desktop\out\bin\Release\net10.0'
$out = Join-Path $root "artifacts\playtest\$Name"
New-Item -ItemType Directory -Force $out | Out-Null
Get-ChildItem $out -Filter *.ppm -ErrorAction SilentlyContinue | Remove-Item -ErrorAction SilentlyContinue
Get-Process Emberward.Desktop -ErrorAction SilentlyContinue | Stop-Process -Force

$env:EMBERWARD_SCRIPTED_INPUT = $Script
$env:EMBERWARD_SCRIPT_SCREENSHOT_FRAMES = $Shots
$env:EMBERWARD_SCRIPT_KEEP_AI = '1'
$env:EMBERWARD_SCREENSHOT_DIR = $out
$gameArgs = @('--hidden', '--windowed=1280x720') + ($Extra -split ' ' | Where-Object { $_ })
$p = Start-Process (Join-Path $exeDir 'Emberward.Desktop.exe') -ArgumentList $gameArgs -WorkingDirectory $exeDir `
    -PassThru -RedirectStandardOutput (Join-Path $out 'game.out') -RedirectStandardError (Join-Path $out 'game.err')
$p.WaitForExit($Seconds * 1000) | Out-Null
if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
foreach ($envVar in 'EMBERWARD_SCRIPTED_INPUT', 'EMBERWARD_SCRIPT_SCREENSHOT_FRAMES', 'EMBERWARD_SCRIPT_KEEP_AI', 'EMBERWARD_SCREENSHOT_DIR') {
    Remove-Item "Env:$envVar" -ErrorAction SilentlyContinue
}
"${Name}: $((Get-ChildItem $out -Filter *.ppm).Count) screenshot(s) in $out"
