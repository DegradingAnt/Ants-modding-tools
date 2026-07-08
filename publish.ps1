# Ant's Modding Tools — build a distributable (AMT-17).
#
# Produces a SELF-CONTAINED win-x64 build: AntsModdingTools.exe plus everything it needs, so the target
# machine does NOT need the .NET runtime installed. Output lands in dist\AntsModdingTools\, and a portable
# zip is written next to it. To build a Windows installer, feed dist\AntsModdingTools\ to installer\AntsModdingTools.iss
# (Inno Setup).
#
#   pwsh ./publish.ps1              # self-contained win-x64 + portable zip
#   pwsh ./publish.ps1 -NoZip       # skip the zip
#   pwsh ./publish.ps1 -Rid linux-x64   # a different runtime

param(
    [string]$Rid = "win-x64",
    [switch]$NoZip
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$proj = Join-Path $root "amt/Amt.App/Amt.App.csproj"
$out  = Join-Path $root "dist/AntsModdingTools"

Write-Host "Publishing $Rid (self-contained) ..."
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish $proj -c Release -r $Rid --self-contained `
    -p:PublishSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $out
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

# read the version the build stamped, for the artifact names
$ver = (Select-String -Path $proj -Pattern '<Version>(.*?)</Version>').Matches[0].Groups[1].Value
Write-Host "Built v$ver -> $out"

if (-not $NoZip) {
    $zip = Join-Path $root "dist/AntsModdingTools-$ver-$Rid.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip
    Write-Host "Portable zip -> $zip"
}
