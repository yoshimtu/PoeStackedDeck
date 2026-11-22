param([string] $Destination = (Join-Path $PSScriptRoot 'bin'))
$ErrorActionPreference = 'Stop'
$manifest = Get-Content -Raw (Join-Path $PSScriptRoot 'plugin.json') | ConvertFrom-Json
$stage = Join-Path $PSScriptRoot ('bin/package-' + [guid]::NewGuid())

dotnet build (Join-Path $PSScriptRoot 'StackedDeckTracker.csproj') -c Release
if ($LASTEXITCODE) { throw 'Plugin build failed.' }

New-Item -ItemType Directory $stage | Out-Null
try {
    Get-ChildItem (Join-Path $PSScriptRoot 'bin/Release/net10.0-windows') |
        Where-Object { $_.Name -notlike 'PluginHost.*' } |
        Copy-Item -Destination $stage -Recurse
    Copy-Item (Join-Path $PSScriptRoot 'plugin.json') $stage

    $entryPath = Join-Path $stage $manifest.entry
    if (!(Test-Path $entryPath)) { throw "Manifest entry '$($manifest.entry)' is missing." }

    New-Item -Force -ItemType Directory $Destination | Out-Null

    # 1. Standard API 1 package (*.plugin.zip)
    $archive = Join-Path $Destination ($manifest.id + '.plugin.zip')
    $temp = Join-Path $Destination ([guid]::NewGuid().ToString() + '.zip')
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $temp
    Move-Item -Force -LiteralPath $temp -Destination $archive

    # 2. Legacy loose plugin DLL (*.plugin.dll)
    $looseDll = Join-Path $Destination 'StackedDeckTracker.plugin.dll'
    Copy-Item -Force -LiteralPath $entryPath -Destination $looseDll

    Write-Output "Package:   $archive"
    Write-Output "Loose DLL: $looseDll"
}
finally {
    if (Test-Path $stage) {
        $stagePath = [IO.Path]::GetFullPath($stage)
        $binRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'bin')) + [IO.Path]::DirectorySeparatorChar
        if (!$stagePath.StartsWith($binRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing to remove a staging directory outside bin.'
        }
        Remove-Item -LiteralPath $stagePath -Recurse -Force -ErrorAction SilentlyContinue
    }
}
