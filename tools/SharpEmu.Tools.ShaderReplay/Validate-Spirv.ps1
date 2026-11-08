# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$DumpRoot,
    [Parameter(Mandatory=$true)][string]$OutputPrefix,
    [string]$Validator = "$env:VULKAN_SDK/Bin/spirv-val.exe",
    [string]$Target = 'vulkan1.3',
    [string[]]$Modes = @()
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$protected = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (@(Get-Process SharpEmu -ErrorAction SilentlyContinue | Where-Object {
    !$_.Path -or $_.Path.StartsWith($protected, [StringComparison]::OrdinalIgnoreCase)
}).Count) { throw 'Worktree emulator running or its path cannot be inspected.' }
$validatorPath = (Resolve-Path -LiteralPath $Validator).Path
$rows = [Collections.Generic.List[object]]::new()
foreach ($mode in Get-ChildItem -LiteralPath $DumpRoot -Directory) {
    if ($Modes.Count -ne 0 -and $mode.Name -notin $Modes) { continue }
    $manifest = Get-Content -LiteralPath (Join-Path $mode.FullName 'manifest.json') -Raw | ConvertFrom-Json
    foreach ($stage in $manifest.Stages) {
        $path = Join-Path $mode.FullName $stage.File
        $sha = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($sha -ne $stage.Sha256) { throw "SHA mismatch: $path" }
        $start = [Diagnostics.ProcessStartInfo]::new($validatorPath)
        $start.Arguments = "--target-env $Target `"$path`""
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $process = [Diagnostics.Process]::Start($start)
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $rows.Add([pscustomobject]@{
            Mode=$mode.Name; Id=$stage.Id; Hash=$stage.Hash; Kind=$stage.Kind
            Sha256=$sha; Exit=$process.ExitCode; Diagnostic=($stdout.Result + $stderr.Result).Trim()
        })
        $process.Dispose()
        if ($rows.Count % 100 -eq 0) { Write-Output "Validated $($rows.Count) modules" }
    }
}
$rows | Export-Csv -LiteralPath ($OutputPrefix + '.csv') -NoTypeInformation
$failures = @($rows | Where-Object Exit -ne 0)
ConvertTo-Json -InputObject $failures -Depth 5 | Set-Content -LiteralPath ($OutputPrefix + '.failures.json') -Encoding UTF8
foreach ($group in $rows | Group-Object Mode) {
    Write-Output "$($group.Name): $($group.Count) modules, $(@($group.Group | Where-Object Exit -ne 0).Count) failures"
}
Write-Output "Validator: $validatorPath; target: $Target; failures: $($failures.Count)"
