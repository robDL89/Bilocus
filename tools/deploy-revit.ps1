# SPDX-License-Identifier: MIT
# Copyright (c) 2026 Roberto Dolfini

param(
    [ValidateSet("2024","2025","2026","2027")]
    [string]$RevitVersion = "2025"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\Bilocus.Revit\Bilocus.Revit.csproj"
$manifest = Join-Path $root "src\Bilocus.Revit\Bilocus.addin"
$addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
$dest = Join-Path $addins "Bilocus"

if (Get-Process -Name "Revit" -ErrorAction SilentlyContinue) {
    throw "Revit is running: close it before deploying, the DLLs are locked."
}

if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
New-Item -ItemType Directory -Path $dest -Force | Out-Null

dotnet build $project -p:RevitVersion=$RevitVersion -o $dest
if ($LASTEXITCODE -ne 0) { throw "Build failed for Revit $RevitVersion" }

if (-not (Test-Path $addins)) { New-Item -ItemType Directory -Path $addins -Force | Out-Null }
Copy-Item $manifest $addins -Force

Write-Output "Deployed to $dest for Revit $RevitVersion"
