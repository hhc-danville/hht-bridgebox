<#
  HHT Bridge Console — build, test and publish (run in PowerShell on Windows 10/11).

    .\build.ps1            # restore, build, run core tests
    .\build.ps1 -Publish   # also produce a self-contained folder in .\publish\
    .\build.ps1 -Run       # build and launch the console
#>
param([switch]$Publish, [switch]$Run)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK not found. Install the .NET 10 SDK: winget install Microsoft.DotNet.SDK.10"
}
$sdk = (dotnet --list-sdks) -join "`n"
if ($sdk -notmatch '^10\.') { Write-Warning "No .NET 10 SDK detected. Found:`n$sdk" }

Write-Host "== Restore & build ==" -ForegroundColor Cyan
dotnet build HHT.BridgeBox.sln -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

Write-Host "== Core tests ==" -ForegroundColor Cyan
dotnet run --project tests\HHT.Bridge.Core.Tests -c Release --no-build
if ($LASTEXITCODE -ne 0) { throw "Tests failed." }

if ($Publish) {
    Write-Host "== Publish (self-contained, win-x64) ==" -ForegroundColor Cyan
    dotnet publish src\HHT.Bridge.TechConsole -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -o publish\bridge
    if ($LASTEXITCODE -ne 0) { throw "Publish failed." }
    Write-Host "Published to $PSScriptRoot\publish\bridge\HHTBridgeConsole.exe (UNSIGNED dev build). Copy the whole bridge folder next to HHC-TechTool.exe." -ForegroundColor Green
}

if ($Run) {
    & "src\HHT.Bridge.TechConsole\bin\Release\net10.0-windows10.0.19041.0\HHTBridgeConsole.exe"
}
