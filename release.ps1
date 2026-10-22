# Builds the Pulse installer: tests -> self-contained publish -> Inno Setup -> artifacts\PulseSetup-<version>.exe
#
#   powershell -ExecutionPolicy Bypass -File .\release.ps1                  # version from Directory.Build.props
#   powershell -ExecutionPolicy Bypass -File .\release.ps1 -Version 0.2.0
#   powershell -ExecutionPolicy Bypass -File .\release.ps1 -Upload          # also create a GitHub release (gh CLI)
#
# Needs: .NET 10 SDK, Inno Setup 6 (winget install JRSoftware.InnoSetup), gh CLI for -Upload.
param(
    [string]$Version,
    [switch]$Upload
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not $Version) {
    $Version = (Select-Xml -Path Directory.Build.props -XPath '//Version').Node.InnerText
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3 (got '$Version')." }

$artifacts = Join-Path $PSScriptRoot 'artifacts'
$publishDir = Join-Path $artifacts 'publish'
$setup = Join-Path $artifacts "PulseSetup-$Version.exe"

function Find-Iscc {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    $onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    throw 'Inno Setup not found. Install it: winget install JRSoftware.InnoSetup'
}
$iscc = Find-Iscc

Write-Host "==> Pulse $Version" -ForegroundColor Cyan

Write-Host '==> Running tests' -ForegroundColor Cyan
dotnet test tests/Pulse.Core.Tests -c Release
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; not releasing.' }

Write-Host '==> Publishing (self-contained, so users need no .NET install)' -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish src/Pulse.App -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:Version=$Version -p:DebugType=none -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

Write-Host '==> Building installer' -ForegroundColor Cyan
& $iscc /Qp "/DAppVersion=$Version" "/DPublishDir=$publishDir" installer\Pulse.iss
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed.' }

$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLower()
"$hash  PulseSetup-$Version.exe" | Out-File "$setup.sha256" -Encoding ascii
$sizeMb = [math]::Round((Get-Item $setup).Length / 1MB, 1)
Write-Host "==> $setup ($sizeMb MB)" -ForegroundColor Green
Write-Host "    SHA-256 $hash"

if ($Upload) {
    Write-Host "==> Creating GitHub release v$Version" -ForegroundColor Cyan
    gh release create "v$Version" $setup "$setup.sha256" --title "Pulse $Version" --generate-notes
    if ($LASTEXITCODE -ne 0) { throw 'gh release create failed.' }
}
