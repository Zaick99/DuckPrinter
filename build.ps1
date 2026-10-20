#Requires -Version 5.1
<#
.SYNOPSIS  Compila Duck Printer Manager su Windows: core Rust + test + publish .NET.
.EXAMPLE   .\build.ps1                 # dist\ framework-dependent (serve .NET 8 Desktop Runtime)
.EXAMPLE   .\build.ps1 -SelfContained  # dist\ con runtime incluso (nessun prerequisito sui PC)
#>
param([switch]$SelfContained, [switch]$SkipTests, [string]$Out = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

Write-Host '==> duck_core (Rust)' -ForegroundColor Yellow
cargo build --release --manifest-path native\duck_core\Cargo.toml
if ($LASTEXITCODE) { throw 'cargo build fallito' }

if (-not $SkipTests) {
    Write-Host '==> test Rust' -ForegroundColor Yellow
    cargo test --manifest-path native\duck_core\Cargo.toml
    if ($LASTEXITCODE) { throw 'test Rust falliti' }
    Write-Host '==> test .NET' -ForegroundColor Yellow
    Copy-Item native\duck_core\target\release\duck_core.dll tests\DuckPrinterManager.Tests\ -Force
    dotnet test tests\DuckPrinterManager.Tests
    if ($LASTEXITCODE) { throw 'test .NET falliti' }
}

Write-Host '==> publish' -ForegroundColor Yellow
$sc = if ($SelfContained) { 'true' } else { 'false' }
dotnet publish src\DuckPrinterManager -c Release -o $Out --self-contained $sc
if ($LASTEXITCODE) { throw 'dotnet publish fallito' }
Copy-Item native\duck_core\target\release\duck_core.dll $Out -Force
Write-Host "Pronto: $Out\DuckPrinterManager.exe" -ForegroundColor Green
