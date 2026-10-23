# Reference existing shared Release DLLs; no shared-source compilation.
param(
    [string]$GodotRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$SharedRoot,
    [string]$FmodDirectory,
    [ValidateSet('win-x64', 'linux-x64', 'osx-x64')][string]$RuntimeIdentifier
)
$ErrorActionPreference = 'Stop'
if (-not $RuntimeIdentifier) {
    $platform = if ($IsWindows) { 'win' } elseif ($IsLinux) { 'linux' } elseif ($IsMacOS) { 'osx' } else { throw 'Unsupported operating system.' }
    $architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
    if ($architecture -ne 'x64') { throw 'The FMOD 1.10 runtime requires an x64 host; on Apple silicon use x64 Godot and PowerShell under Rosetta.' }
    $RuntimeIdentifier = "$platform-$architecture"
}
$windowsTarget = $RuntimeIdentifier.StartsWith('win-', [StringComparison]::Ordinal)
function Get-NativeFileName([string]$Name) {
    if ($windowsTarget) { return "$Name.dll" }
    if ($Name -eq 'fmod64') { $Name = 'fmod' }
    $prefix = if ($Name.StartsWith('lib', [StringComparison]::Ordinal)) { '' } else { 'lib' }
    $suffix = if ($RuntimeIdentifier.StartsWith('osx-', [StringComparison]::Ordinal)) { '.dylib' } else { '.so' }
    return "$prefix$Name$suffix"
}
$godotPath = (Resolve-Path -LiteralPath $GodotRoot).Path
$workspacePath = Split-Path -Parent $godotPath
if (-not $SharedRoot) { $SharedRoot = Join-Path $workspacePath 'Shared' }
$collector = Join-Path $workspacePath 'Tools/Get-PortableBuiltAssemblyClosure.ps1'
$outputPath = Join-Path $godotPath '.godot/SharedAssemblies'
$closure = & $collector -SharedRoot $SharedRoot -OutputDirectory $outputPath -Configuration Release
$document = [xml]'<Project><ItemGroup /></Project>'
$items = $document.Project.SelectSingleNode('ItemGroup')
foreach ($assembly in $closure.Assemblies) {
    if ((Get-FileHash -LiteralPath $assembly.Path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $assembly.SHA256) {
        throw "The shared DLL changed during synchronization: $($assembly.Path)"
    }
    $reference = $document.CreateElement('Reference')
    $reference.SetAttribute('Include', [IO.Path]::GetFileNameWithoutExtension($assembly.FileName))
    $hint = $document.CreateElement('HintPath'); $hint.InnerText = $assembly.Path
    [void]$reference.AppendChild($hint)
    $copy = $document.CreateElement('Private'); $copy.InnerText = 'true'
    [void]$reference.AppendChild($copy)
    if ($assembly.FileName -cin @('MonoMod.Backports.dll', 'JetBrains.Annotations.dll')) {
        $alias = $document.CreateElement('Aliases'); $alias.InnerText = 'SharedRuntime'
        [void]$reference.AppendChild($alias)
    }
    [void]$items.AppendChild($reference)
}
$runtime = @($closure.Assemblies | Where-Object FileName -ceq 'Celeste.Game.Runtime.dll')
if ($runtime.Count -ne 1) { throw 'Expected exactly one Celeste.Game.Runtime.dll.' }
$nativeSourceDirectory = Join-Path $workspacePath "artifacts/runtime/$RuntimeIdentifier"
if (-not $FmodDirectory) { $FmodDirectory = $nativeSourceDirectory }
$nativeDestination = Join-Path $godotPath "Native/$RuntimeIdentifier"
$nativeSources = [ordered]@{}
foreach ($name in @('SDL2', 'FAudio', 'FNA3D', 'libtheorafile', 'lua54')) {
    $file = Get-NativeFileName $name
    $nativeSources[$file] = Join-Path $nativeSourceDirectory $file
}
foreach ($name in @('fmod64', 'fmodstudio')) {
    $file = Get-NativeFileName $name
    $nativeSources[$file] = Join-Path $FmodDirectory $file
}
if ($RuntimeIdentifier.StartsWith('linux-', [StringComparison]::Ordinal)) {
    $file = Get-NativeFileName 'fmod_SDL'
    $nativeSources[$file] = Join-Path $nativeSourceDirectory $file
}
$graphicsNames = @('CelesteGraphicsEffects', 'CelesteGraphicsVulkan')
if ($windowsTarget) { $graphicsNames += @('CelesteGraphicsDirect3D12', 'CelesteGraphicsD3D11') }
foreach ($name in $graphicsNames) {
    $file = Get-NativeFileName $name
    $nativeSources[$file] = Join-Path $SharedRoot "artifacts/runtime/$RuntimeIdentifier/$file"
}
if (-not $windowsTarget) {
    foreach ($directory in @($nativeSourceDirectory, $FmodDirectory)) {
        foreach ($dependency in Get-ChildItem -LiteralPath $directory -File | Where-Object { $_.Name -like '*.so*' -or $_.Name -like '*.dylib' }) {
            if (-not $nativeSources.Contains($dependency.Name)) { $nativeSources[$dependency.Name] = $dependency.FullName }
        }
    }
}
# Validate all inputs before publishing this exact native closure.
$nativeLibraries = foreach ($entry in $nativeSources.GetEnumerator()) {
    if (-not (Test-Path -LiteralPath $entry.Value -PathType Leaf)) { throw "Built native library missing: $($entry.Value)" }
    [pscustomobject]@{
        FileName = $entry.Key
        Path = [IO.Path]::GetFullPath($entry.Value)
        Destination = Join-Path $nativeDestination $entry.Key
        SHA256 = (Get-FileHash -LiteralPath $entry.Value -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
[IO.Directory]::CreateDirectory($nativeDestination) | Out-Null
foreach ($library in $nativeLibraries) {
    Copy-Item -LiteralPath $library.Path -Destination $library.Destination -Force
    if ((Get-FileHash -LiteralPath $library.Destination -Algorithm SHA256).Hash.ToLowerInvariant() -cne $library.SHA256) {
        throw "Published native library differs from its reviewed source: $($library.FileName)"
    }
    $asset = $document.CreateElement('None'); $asset.SetAttribute('Include', $library.Destination)
    foreach ($property in @{
            Link = "Native/$RuntimeIdentifier/$($library.FileName)"
            CopyToOutputDirectory = 'PreserveNewest'
            CopyToPublishDirectory = 'PreserveNewest'
        }.GetEnumerator()) {
        $element = $document.CreateElement($property.Key); $element.InnerText = $property.Value
        [void]$asset.AppendChild($element)
    }
    [void]$items.AppendChild($asset)
}
$normalizer = Join-Path $workspacePath 'Tools/Normalize-NativeLibraries.ps1'
& $normalizer -Directory $nativeDestination -RuntimeIdentifier $RuntimeIdentifier
if (-not $windowsTarget) {
    $target = $document.CreateElement('Target')
    $target.SetAttribute('Name', 'NormalizeCelesteNativeModuleAliases')
    $target.SetAttribute('AfterTargets', 'CopyFilesToOutputDirectory;Publish')
    foreach ($directoryProperty in @('TargetDir', 'PublishDir')) {
        $property = '$(' + $directoryProperty + ')'
        $destination = $property + 'Native/' + $RuntimeIdentifier
        $execute = $document.CreateElement('Exec')
        $execute.SetAttribute('Condition', "'$property' != '' And Exists('$destination')")
        $execute.SetAttribute('Command', 'pwsh -NoLogo -NoProfile -File "' + $normalizer + '" -Directory "' + $destination + '" -RuntimeIdentifier ' + $RuntimeIdentifier)
        [void]$target.AppendChild($execute)
    }
    [void]$document.Project.AppendChild($target)
}
$closure | Add-Member -NotePropertyName RuntimeIdentifier -NotePropertyValue $RuntimeIdentifier -Force
$closure | Add-Member -NotePropertyName NativeLibraries -NotePropertyValue @($nativeLibraries) -Force
$closure | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $outputPath 'PortableBuiltAssemblyClosure.json') -Encoding utf8
$propsPath = Join-Path $outputPath 'SharedAssemblies.props'
$document.Save($propsPath)
Write-Output "Godot references $($closure.Assemblies.Count) existing Release DLLs: $propsPath"
