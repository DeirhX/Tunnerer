# The playtest regression set (seed 42, AI on). Takes about 4.5 minutes.
#  relay : dig out, build a relay, check the link preview, plant it (it links), heat the ground so it pumps
#  exit  : get killed once, respawn, then drive out of the base exit under the respawn shield
#  duck  : sit still until killed, through respawns, to the DEFEAT screen
#
# Run detached when the calling shell may be killed early (e.g. from an automation tool):
#   Invoke-CimMethod Win32_Process Create -Arguments @{ CommandLine = 'powershell -File tools\playtest\regression.ps1' }
$scenario = Join-Path $PSScriptRoot 'scenario.ps1'

$osc = ((1..14) | ForEach-Object { '-1,0,1:10;1,0,1:10' }) -join ';'
$relay = "0,0:12;0,1:36;1,0:144;0,0,0,1:2;0,0:4;1,0:24;0,-1:24;0,0,0,1:2;0,0:4;$osc;-1,0:120;0,0:240"
$relayShots = (@((0..30) | ForEach-Object { 12 + $_ * 24 }) + @(200, 230, 262, 300)) -join ','
& $scenario -Name relay -Script $relay -Shots $relayShots -Seconds 36

$exit = '0,1:72;0,0:1008;0,1:60;-1,0:240;0,0:900'
$exitShots = ((0..36) | ForEach-Object { 720 + $_ * 24 }) -join ','
& $scenario -Name exit -Script $exit -Shots $exitShots -Seconds 72

$duck = '0,1:72;0,0:6000'
$duckShots = ((0..74) | ForEach-Object { 24 + $_ * 48 }) -join ','
& $scenario -Name duck -Script $duck -Shots $duckShots -Seconds 160
