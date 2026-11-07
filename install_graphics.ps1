param([string]$GameDir, [switch]$DryRun)

$ErrorActionPreference = 'Stop'
$package = Split-Path -Parent $MyInvocation.MyCommand.Path
$manifest = Get-Content -LiteralPath (Join-Path $package 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$assetZip = Join-Path $package 'assets.zip'
$packs = @('bak.pak', 'char.pak', 'grp.pak', 'IC/bak.pak', 'IC/char.pak', 'IC/grp.pak')

if ([string]::IsNullOrWhiteSpace($GameDir)) {
    Write-Host 'Enter the folder containing the Japanese WA2.exe:'
    $GameDir = Read-Host 'Game folder'
}
$GameDir = $GameDir.Trim(' ', '"')
if (-not (Test-Path -LiteralPath $GameDir -PathType Container)) {
    throw "Game folder does not exist: $GameDir"
}
$game = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $GameDir).Path)
if (-not (Test-Path -LiteralPath (Join-Path $game 'WA2.exe') -PathType Leaf)) {
    throw 'WA2.exe was not found in the selected folder.'
}
if (-not $DryRun -and (Get-Process -Name 'WA2' -ErrorAction SilentlyContinue)) {
    throw 'Close the game before installing graphics.'
}
if (-not (Test-Path -LiteralPath $assetZip -PathType Leaf)) {
    throw 'Graphics assets.zip is missing.'
}
$actualAssetHash = (Get-FileHash -LiteralPath $assetZip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualAssetHash -ne $manifest.assets_sha256) {
    throw 'Graphics assets.zip is damaged. No game files were changed.'
}
$listedPacks = @($manifest.archives.PSObject.Properties.Name | Sort-Object)
if (@(Compare-Object $listedPacks @($packs | Sort-Object)).Count -ne 0) {
    throw 'Graphics manifest has an unexpected archive list.'
}

Add-Type -Path (Join-Path $package 'kcap_patch.cs') `
    -ReferencedAssemblies @('System.IO.Compression.dll', 'System.IO.Compression.FileSystem.dll')

$work = @()
foreach ($pack in $packs) {
    $archive = Join-Path $game $pack
    if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
        throw "Required game archive is missing: $pack. No files were changed."
    }
    $section = $manifest.archives.PSObject.Properties[$pack].Value
    $entries = @($section.entries)
    [string[]]$names = @($entries | ForEach-Object { [string]$_.name })
    [string[]]$members = @($entries | ForEach-Object { [string]$_.member })
    [string[]]$hashes = @($entries | ForEach-Object { [string]$_.sha256 })
    [long[]]$sizes = @($entries | ForEach-Object { [long]$_.size })
    $pending = [Wa2Graphics.KcapPatch]::Check(
        $archive, $assetZip, [int]$section.entry_count, [string]$section.name_table_sha256,
        $names, $members, $hashes, $sizes)
    $work += [pscustomobject]@{
        Pack = $pack; Archive = $archive; Section = $section
        Names = $names; Members = $members; Hashes = $hashes; Sizes = $sizes
        Pending = $pending
    }
    Write-Host "$pack`: $pending entries to update"
}
if ($DryRun) {
    Write-Host 'Dry run complete. No game files were changed.'
    exit 0
}
if (@($work | Where-Object { $_.Pending -gt 0 }).Count -eq 0) {
    Write-Host 'All graphics are already installed. No game files were changed.'
    exit 0
}

$backup = Join-Path $game ('WA2_Graphics_Backup_' + (Get-Date -Format 'yyyyMMdd_HHmmss'))
if (Test-Path -LiteralPath $backup) {
    throw "Backup directory already exists: $backup"
}
$moved = @()
try {
    foreach ($item in $work) {
        if ($item.Pending -eq 0) { continue }
        $stage = $item.Archive + '.graphics_stage'
        if (Test-Path -LiteralPath $stage) {
            throw "Staging file already exists: $stage"
        }
        $updated = [Wa2Graphics.KcapPatch]::Stage(
            $item.Archive, $stage, $assetZip,
            [int]$item.Section.entry_count, [string]$item.Section.name_table_sha256,
            $item.Names, $item.Members, $item.Hashes, $item.Sizes)
        if ($updated -ne $item.Pending) {
            throw "Archive changed during installation: $($item.Pack)"
        }
        $backupFile = Join-Path $backup $item.Pack
        $parent = Split-Path -Parent $backupFile
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        Move-Item -LiteralPath $item.Archive -Destination $backupFile
        $moved += [pscustomobject]@{ Archive = $item.Archive; Backup = $backupFile; Stage = $stage }
        Move-Item -LiteralPath $stage -Destination $item.Archive
        Write-Host "$($item.Pack)`: installed $updated entries"
    }
    $receipt = @{
        version = $manifest.version
        installed = (Get-Date).ToString('o')
        backup = $backup
        updated = @($work | Where-Object { $_.Pending -gt 0 } | ForEach-Object {
            @{ archive = $_.Pack; entries = $_.Pending }
        })
    }
    $receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $game 'WA2_Graphics_install.json') -Encoding UTF8
} catch {
    $failure = $_
    for ($number = $moved.Count - 1; $number -ge 0; $number--) {
        $item = $moved[$number]
        if (Test-Path -LiteralPath $item.Archive) {
            Remove-Item -LiteralPath $item.Archive -Force
        }
        if (Test-Path -LiteralPath $item.Backup) {
            Move-Item -LiteralPath $item.Backup -Destination $item.Archive
        }
    }
    foreach ($item in $work) {
        $stage = $item.Archive + '.graphics_stage'
        if (Test-Path -LiteralPath $stage) {
            Remove-Item -LiteralPath $stage -Force
        }
    }
    throw $failure
}

Write-Host "Graphics installed in: $game"
Write-Host "Original archives saved in: $backup"
