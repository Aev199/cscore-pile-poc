# For a development PC with Git + .NET 10 SDK. Not required to run the published Windows EXE.
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root
if (!(Test-Path 'vendor/OpenCS/.git')) {
    git clone https://github.com/rumata-ap/OpenCS.git vendor/OpenCS
    if ($LASTEXITCODE -ne 0) { throw 'Git clone failed' }
}
git -C vendor/OpenCS fetch origin 4ae6aa077be5b29145930ec093ba1c9eb7776222
if ($LASTEXITCODE -ne 0) { throw 'Git fetch failed' }
git -C vendor/OpenCS checkout --detach 4ae6aa077be5b29145930ec093ba1c9eb7776222
if ($LASTEXITCODE -ne 0) { throw 'Git checkout failed' }
python tools/apply_opencs_patches.py
if ($LASTEXITCODE -ne 0) { throw 'Pinned CScore patch failed' }
dotnet publish src/CScorePileRunner/CScorePileRunner.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
Write-Host 'Ready: publish/CScorePileRunner.exe'
