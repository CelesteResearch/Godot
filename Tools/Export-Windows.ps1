param([string]$GodotExecutable, [string]$FmodDirectory, [string]$LogDirectory)
& (Join-Path $PSScriptRoot 'Export.ps1') -RuntimeIdentifier win-x64 @PSBoundParameters
