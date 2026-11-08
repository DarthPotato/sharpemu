# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
[CmdletBinding()]
param(
    [string]$OutputRoot = '.tmp/native-build',
    [string]$Version = '0.0.6-mono',
    [string]$PackageRoot = '',
    [string]$Project = "$PSScriptRoot/SharpEmu.Tools.ShaderReplay.csproj"
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$destination = [IO.Path]::GetFullPath((Join-Path $repo $OutputRoot))
$protected = Join-Path $repo 'artifacts'
$active = @(Get-Process SharpEmu -ErrorAction SilentlyContinue | Where-Object {
    !$_.Path -or $_.Path.StartsWith($protected + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
})
if ($active.Count) { throw 'An emulator runs from this worktree artifacts directory (or its path cannot be inspected).' }
if ($destination.Equals($protected, [StringComparison]::OrdinalIgnoreCase) -or
    $destination.StartsWith($protected + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputRoot must not be the running emulator artifacts directory'
}
New-Item -ItemType Directory -Force -Path $destination | Out-Null
$taskAssembly = Join-Path $destination 'artifacts/bin/Release/netstandard2.0/SharpEmu.SourceGenerators.dll'
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
$packageArgs = @()
if ($PackageRoot) { $packageArgs = @("-p:RestorePackagesPath=$([IO.Path]::GetFullPath($PackageRoot))") }
& dotnet build $Project -c Release -m:1 --nologo `
    "-p:RepoRoot=$destination/" "-p:Version=$Version" `
    "-p:SharpEmuAerolibTaskAssembly=$taskAssembly" `
    "-p:CustomAfterMicrosoftCommonTargets=$PSScriptRoot/IsolatedBuild.targets" `
    -p:NuGetAudit=false -p:UseSharedCompilation=false @packageArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }
Write-Output "Output: $destination/artifacts/bin/Release/net10.0/"
