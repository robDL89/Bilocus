# SPDX-License-Identifier: MIT
# Copyright (c) 2026 Roberto Dolfini

# Builds the release zips in dist\:
#   Bilocus-Revit2024.zip  Bilocus.addin + Bilocus\ (net48 build)
#   Bilocus-Revit2025.zip  Bilocus.addin + Bilocus\ (net8.0-windows build)
#   Bilocus-Blender.zip    bilocus\ (add-on folder, installable from disk)
# Every zip carries the license files that apply to its content.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist"
$stage = Join-Path $dist "stage"

Add-Type -AssemblyName System.IO.Compression.FileSystem

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

function New-Zip($sourceDir, $zipPath) {
    # .NET writes forward slashes in entry names, which the Python zipfile
    # module used by Blender expects. Compress-Archive on PowerShell 5.1
    # writes backslashes instead.
    [System.IO.Compression.ZipFile]::CreateFromDirectory($sourceDir, $zipPath)
    Write-Output "Created $zipPath"
}

# Revit add-in, one zip per Revit version.
$project = Join-Path $root "src\Bilocus.Revit\Bilocus.Revit.csproj"
$manifest = Join-Path $root "src\Bilocus.Revit\Bilocus.addin"
foreach ($version in @("2024", "2025")) {
    $top = Join-Path $stage "revit-$version"
    $bin = Join-Path $top "Bilocus"
    New-Item -ItemType Directory -Path $bin -Force | Out-Null

    dotnet build $project -c Release -p:RevitVersion=$version -o $bin
    if ($LASTEXITCODE -ne 0) { throw "Build failed for Revit $version" }

    # Debug symbols are not needed by users.
    Get-ChildItem $bin -Filter *.pdb | Remove-Item -Force

    Copy-Item $manifest $top
    Copy-Item (Join-Path $root "LICENSE.txt") $bin
    Copy-Item (Join-Path $root "LICENSES\MIT.txt") $bin
    Copy-Item (Join-Path $root "TERMS-OF-USE.md") $bin
    Copy-Item (Join-Path $root "THIRD-PARTY-NOTICES.txt") $bin

    New-Zip $top (Join-Path $dist "Bilocus-Revit$version.zip")
}

# Blender add-on: the zip must contain the "bilocus" folder at its root.
$blenderTop = Join-Path $stage "blender"
$addon = Join-Path $blenderTop "bilocus"
New-Item -ItemType Directory -Path $addon -Force | Out-Null
Copy-Item (Join-Path $root "src\blender_addon\*.py") $addon
Copy-Item (Join-Path $root "LICENSES\GPL-3.0-or-later.txt") (Join-Path $addon "LICENSE.txt")
New-Zip $blenderTop (Join-Path $dist "Bilocus-Blender.zip")

Remove-Item $stage -Recurse -Force
Write-Output "Release files in $dist"
