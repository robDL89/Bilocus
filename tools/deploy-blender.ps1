# SPDX-License-Identifier: MIT
# Copyright (c) 2026 Roberto Dolfini

param(
    [ValidateSet("5.1","5.2")]
    [string]$BlenderVersion = "5.2"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root "src\blender_addon"
$userRoot = Join-Path $env:APPDATA "Blender Foundation\Blender\$BlenderVersion"
$addonsRoot = Join-Path $userRoot "scripts\addons"
$target = Join-Path $addonsRoot "bilocus"

if (-not (Test-Path $source)) { throw "Missing source: $source" }

# Blender creates the version's user folder on first launch: if it is
# missing, that version has never been run and installing the addon
# would not do anything.
if (-not (Test-Path $userRoot)) {
    throw "Blender $BlenderVersion has no user folder: $userRoot. Install it and run it at least once before deploying."
}

# scripts\addons, on the other hand, does not necessarily exist: since
# Blender 4.2 addons go through the extensions system, and the legacy path
# is only created by installing an old-style addon from the UI. Blender
# scans it anyway - addon_utils.py builds it as <user_scripts>\addons -
# so it can be created by hand.
if (-not (Test-Path $addonsRoot)) {
    New-Item -ItemType Directory -Path $addonsRoot -Force | Out-Null
    Write-Output "Created the legacy addons folder: $addonsRoot"
}
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
New-Item -ItemType Directory -Path $target -Force | Out-Null

Copy-Item (Join-Path $source "*.py") $target -Force
Write-Output "Addon copied to $target"
Write-Output ""
Write-Output "RESTART BLENDER. Disabling and re-enabling the addon is NOT enough:"
Write-Output "the already-imported modules stay in memory and keep running."
Write-Output "Typical symptom if you skip this: everything works but behaves like"
Write-Output "the previous version, with no error at all."
