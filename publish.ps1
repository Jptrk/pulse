# Builds a release copy of Pulse into .\publish (needs the .NET 10 SDK; runs on the .NET 10 Desktop Runtime).
# Usage: powershell -ExecutionPolicy Bypass -File .\publish.ps1
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet test tests/Pulse.Core.Tests -c Release
if ($LASTEXITCODE -ne 0) { throw 'Tests failed; not publishing.' }

dotnet publish src/Pulse.App -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -o publish
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

Write-Host ''
Write-Host "Published to $PSScriptRoot\publish\Pulse.exe"
