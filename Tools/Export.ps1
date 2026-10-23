param(
    [string] $GodotExecutable,
    [string] $FmodDirectory,
    [string] $LogDirectory,
    [ValidateSet('win-x64', 'linux-x64', 'osx-x64')][string] $RuntimeIdentifier
)

$ErrorActionPreference = 'Stop'
if (-not $RuntimeIdentifier) {
    $platform = if ($IsWindows) { 'win' } elseif ($IsLinux) { 'linux' } elseif ($IsMacOS) { 'osx' } else { throw 'Unsupported operating system.' }
    if ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
        throw 'The FMOD 1.10 runtime requires an x64 host; on Apple silicon use x64 Godot and PowerShell under Rosetta.'
    }
    $RuntimeIdentifier = "$platform-x64"
}
if ($RuntimeIdentifier.StartsWith('linux-', [StringComparison]::Ordinal) -and -not $IsLinux) {
    throw 'Finalize Linux exports on Linux so native canonical and SONAME aliases retain one physical module.'
}
if ($RuntimeIdentifier.StartsWith('osx-', [StringComparison]::Ordinal) -and -not $IsMacOS) {
    throw 'Finalize macOS exports on macOS to preserve native library aliases and sign the completed app bundle.'
}
$windowsTarget = $RuntimeIdentifier.StartsWith('win-', [StringComparison]::Ordinal)
$macTarget = $RuntimeIdentifier.StartsWith('osx-', [StringComparison]::Ordinal)
$preset = if ($windowsTarget) { 'Windows Desktop' } elseif ($macTarget) { 'macOS' } else { 'Linux' }
function Copy-ContentTree([string]$Source, [string]$Destination) {
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $Source -File -Recurse | Where-Object Extension -cne '.meta') {
        $relative = [IO.Path]::GetRelativePath($Source, $file.FullName)
        $target = Join-Path $Destination $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
}
if (-not $GodotExecutable) { $GodotExecutable = $env:GODOT_MONO_EXECUTABLE }
if (-not $GodotExecutable) {
    $godotCommand = Get-Command 'godot-mono', 'godot4-mono', 'godot', 'godot4' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $godotCommand) {
        throw 'Pass -GodotExecutable with the Godot 4.5.1 Mono executable, set GODOT_MONO_EXECUTABLE, or add Godot Mono to PATH.'
    }
    $GodotExecutable = $godotCommand.Source
}
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot '..'))
$buildsRoot = Join-Path $workspaceRoot 'Builds'
$outputDirectory = Join-Path $buildsRoot "Godot/$RuntimeIdentifier"
$stageDirectory = Join-Path $buildsRoot ('.Godot-stage-' + [guid]::NewGuid().ToString('N'))
$backupDirectory = Join-Path $buildsRoot ('.Godot-previous-' + [guid]::NewGuid().ToString('N'))
$executable = Join-Path $stageDirectory $(if ($windowsTarget) { 'CelesteGodot.exe' } elseif ($macTarget) { 'CelesteGodot.app' } else { 'CelesteGodot.x86_64' })
$payloadDirectory = if ($macTarget) { Join-Path $executable 'Contents/MacOS' } else { $stageDirectory }
. (Join-Path $workspaceRoot 'Tools/ContentPaths.ps1')
$contentPaths = Get-CelesteContentPaths -WorkspaceRoot $workspaceRoot
$contentSource = $contentPaths.Game
$everestSource = $contentPaths.Everest
$frameworkTimeSource = $contentPaths.FrameworkTimeResources
$solution = Join-Path $projectRoot 'CelesteGodot.sln'
$createdSolution = $false
& (Join-Path $PSScriptRoot 'Sync-SharedAssemblies.ps1') -GodotRoot $projectRoot -FmodDirectory $FmodDirectory -RuntimeIdentifier $RuntimeIdentifier
if (-not $LogDirectory) {
    $LogDirectory = Join-Path $workspaceRoot 'artifacts/logs/Godot'
}

function Assert-BuildChild([string] $path) {
    $parent = [IO.Path]::GetFullPath($buildsRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $resolved = [IO.Path]::GetFullPath($path)
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (-not $resolved.StartsWith($parent, $comparison)) {
        throw "Build path is not inside $buildsRoot : $resolved"
    }
    return $resolved
}

if (-not (Test-Path -LiteralPath $GodotExecutable -PathType Leaf)) {
    throw "Godot Mono executable missing: $GodotExecutable"
}
if (-not (Test-Path -LiteralPath $contentSource -PathType Container)) {
    throw "Celeste content missing: $contentSource"
}
if (-not (Test-Path -LiteralPath $everestSource -PathType Container)) {
    throw "Everest content missing: $everestSource"
}
if (-not (Test-Path -LiteralPath (Join-Path $frameworkTimeSource 'mscorlib.resources') -PathType Leaf)) {
    throw "Framework time resources missing: $frameworkTimeSource"
}

Assert-BuildChild $stageDirectory | Out-Null
Assert-BuildChild $backupDirectory | Out-Null
Assert-BuildChild $outputDirectory | Out-Null
try {
    if (-not (Test-Path -LiteralPath $solution -PathType Leaf)) {
        & dotnet new sln --name CelesteGodot --format sln --output $projectRoot | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Failed to create temporary Godot solution.' }
        $createdSolution = $true
        & dotnet sln $solution add (Join-Path $projectRoot 'CelesteGodot.csproj') | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Failed to add Godot project to temporary solution.' }
    }
    New-Item -ItemType Directory -Force -Path $stageDirectory | Out-Null
    New-Item -ItemType Directory -Force -Path $LogDirectory | Out-Null
    $exportLog = Join-Path $LogDirectory 'GodotExport.log'
    $exportErrorLog = [IO.Path]::ChangeExtension($exportLog, '.stderr.log')
    $arguments = '--headless --path "' + $projectRoot + '" --export-release "' + $preset + '" "' + $executable + '"'
    $startArguments = @{ FilePath = $GodotExecutable; ArgumentList = $arguments; PassThru = $true; RedirectStandardOutput = $exportLog; RedirectStandardError = $exportErrorLog }
    if ($IsWindows) { $startArguments.WindowStyle = 'Hidden' }
    $exportProcess = Start-Process @startArguments
    if (-not $exportProcess.WaitForExit(180000)) {
        Stop-Process -Id $exportProcess.Id -Force
        throw "Godot export timed out; see $exportLog and $exportErrorLog"
    }
    $exportErrors = Select-String -LiteralPath $exportLog, $exportErrorLog -Pattern '^ERROR:'
    if ($exportProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $executable) -or
        $exportErrors) {
        $errors = $exportErrors | Select-Object -First 5 | ForEach-Object Line
        throw "Godot export failed (exit $($exportProcess.ExitCode)): $($errors -join '; ') See $exportLog and $exportErrorLog"
    }
    if (-not (Get-ChildItem -LiteralPath $stageDirectory -Recurse -Filter 'CelesteGodot.dll' -File | Select-Object -First 1)) {
        throw "Godot export has no managed game assembly: $exportLog"
    }

    # Godot publishes its .NET 8 framework assemblies. All other portable dependencies
    # must retain exact bytes.
    $managedClosure = Get-Content -LiteralPath (Join-Path $projectRoot '.godot/SharedAssemblies/PortableBuiltAssemblyClosure.json') -Raw | ConvertFrom-Json
    foreach ($assembly in $managedClosure.Assemblies) {
        $published = @(Get-ChildItem -LiteralPath $stageDirectory -Recurse -File -Filter $assembly.FileName)
        if ($published.Count -ne 1) { throw "Expected one exported managed dependency: $($assembly.FileName)" }
        $identity = [Reflection.AssemblyName]::GetAssemblyName($published[0].FullName)
        if ($identity.Name -cne [IO.Path]::GetFileNameWithoutExtension($assembly.FileName)) {
            throw "Exported managed identity mismatch: $($assembly.FileName)"
        }
        if ($assembly.FileName.StartsWith('System.', [StringComparison]::Ordinal)) {
            # Keep the host runtime's framework implementation; never overwrite it with
            # a portable netstandard dependency merely to make a file hash comparison pass.
            continue
        }
        if ((Get-FileHash -LiteralPath $assembly.Path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $assembly.SHA256 -or
            (Get-FileHash -LiteralPath $published[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant() -cne $assembly.SHA256) {
            throw "Exported portable managed dependency differs from its synchronized source: $($assembly.FileName)"
        }
    }

    $contentDestination = Join-Path $payloadDirectory 'Celeste'
    New-Item -ItemType Directory -Force -Path $contentDestination | Out-Null
    Copy-ContentTree $contentSource $contentDestination

    # The selected workspace content packages are authoritative. Portable publication
    # supplies only packages absent from that content; it cannot replace a newer port.
    $publishedModsDirectory = Join-Path $buildsRoot 'Mods'
    if (Test-Path -LiteralPath $publishedModsDirectory -PathType Container) {
        $publishedMods = @(Get-ChildItem -LiteralPath $publishedModsDirectory -File -Filter '*.zip')
        if ($publishedMods.Count -gt 0) {
            $modsDestination = Join-Path $contentDestination 'Mods'
            New-Item -ItemType Directory -Force -Path $modsDestination | Out-Null
            foreach ($archive in $publishedMods) {
                $selectedArchive = Join-Path (Join-Path $contentSource 'Mods') $archive.Name
                if (Test-Path -LiteralPath $selectedArchive -PathType Leaf) { continue }
                Copy-Item -LiteralPath $archive.FullName -Destination (Join-Path $modsDestination $archive.Name) -Force
            }
        }
    }

    $everestDestination = Join-Path $payloadDirectory 'Everest'
    New-Item -ItemType Directory -Force -Path $everestDestination | Out-Null
    Copy-ContentTree $everestSource $everestDestination

    $frameworkTimeDestination = Join-Path $payloadDirectory 'CelesteFrameworkTimeResources'
    New-Item -ItemType Directory -Force -Path $frameworkTimeDestination | Out-Null
    Copy-ContentTree $frameworkTimeSource $frameworkTimeDestination


    $nativeDestination = Join-Path $payloadDirectory "Native/$RuntimeIdentifier"
    New-Item -ItemType Directory -Force -Path $nativeDestination | Out-Null
    $sharedClosure = Get-Content -LiteralPath (Join-Path $projectRoot '.godot/SharedAssemblies/PortableBuiltAssemblyClosure.json') -Raw | ConvertFrom-Json
    if ($sharedClosure.RuntimeIdentifier -cne $RuntimeIdentifier) { throw 'Synchronized native closure targets a different platform.' }
    $expectedNativeLibraries = @($sharedClosure.NativeLibraries | ForEach-Object FileName)
    foreach ($fileName in $expectedNativeLibraries) {
        $library = @($sharedClosure.NativeLibraries | Where-Object FileName -ceq $fileName)
        if ($library.Count -ne 1) { throw "The synchronized native closure is missing $fileName." }
        $source = $library[0].Destination
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() -cne $library[0].SHA256) {
            throw "The synchronized native library changed before export: $fileName"
        }
        $destination = Join-Path $nativeDestination $fileName
        Copy-Item -LiteralPath $source -Destination $destination -Force
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() -cne $library[0].SHA256) {
            throw "The staged native library differs from its synchronized source: $fileName"
        }
    }
    & (Join-Path $workspaceRoot 'Tools/Normalize-NativeLibraries.ps1') -Directory $nativeDestination -RuntimeIdentifier $RuntimeIdentifier
    $luaLicense = Join-Path $projectRoot "Native/$RuntimeIdentifier/Lua.LICENSE.txt"
    if (Test-Path -LiteralPath $luaLicense -PathType Leaf) {
        Copy-Item -LiteralPath $luaLicense -Destination $nativeDestination -Force
    }

    if (-not (Test-Path -LiteralPath (Join-Path $payloadDirectory 'Celeste/Maps') -PathType Container) -or
        -not (Test-Path -LiteralPath (Join-Path $payloadDirectory 'CelesteFrameworkTimeResources/mscorlib.resources') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $payloadDirectory 'Everest/Core/Graphics/Atlases/Gui/menu/maplist.png') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $payloadDirectory 'Everest/Core/Graphics/Atlases/Gui/menu/mapsearch.png') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $payloadDirectory 'Everest/Core/Dialog/English.txt') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $nativeDestination $(if ($windowsTarget) { 'lua54.dll' } elseif ($macTarget) { 'liblua54.dylib' } else { 'liblua54.so' })) -PathType Leaf)) {
        throw "Incomplete staged Godot export: $stageDirectory"
    }

    function Write-Launchers([string]$Directory) {
        function Write-Launcher([string]$Name, [string]$Arguments) {
            $launcher = "@echo off`r`ncd /d `"%~dp0`"`r`nstart `"`" `"%~dp0CelesteGodot.exe`" -- $Arguments %*`r`n"
            [IO.File]::WriteAllText((Join-Path $Directory $Name), $launcher, [Text.Encoding]::ASCII)
        }
        Write-Launcher 'Start-Original.bat' '--original'
        Write-Launcher 'Start-Everest.bat' '--everest'
        foreach ($backend in @('D3D11', 'Vulkan', 'D3D12')) {
            $argument = '--graphics-backend=' + $backend.ToLowerInvariant()
            Write-Launcher "Start-$backend.bat" "$argument --original"
            Write-Launcher "Start-Everest-$backend.bat" "$argument --everest"
        }
    }
    if ($windowsTarget) { Write-Launchers $stageDirectory }
    else {
        $relativeExecutable = 'CelesteGodot.x86_64'
        if ($macTarget) {
            $info = [xml](Get-Content -LiteralPath (Join-Path $executable 'Contents/Info.plist') -Raw)
            $entry = $info.SelectSingleNode('/plist/dict/key[text()="CFBundleExecutable"]')
            if (-not $entry -or -not $entry.NextSibling -or $entry.NextSibling.Name -cne 'string') { throw 'Exported macOS bundle has no executable identity.' }
            $binaryName = $entry.NextSibling.InnerText
            if ([IO.Path]::GetFileName($binaryName) -cne $binaryName -or -not (Test-Path -LiteralPath (Join-Path $payloadDirectory $binaryName) -PathType Leaf)) { throw 'Exported macOS executable is missing.' }
            $relativeExecutable = 'CelesteGodot.app/Contents/MacOS/' + $binaryName
        }
        foreach ($exportProfile in @('Original', 'Everest')) {
            $launcher = '#!/bin/sh' + "`n" + 'cd -- "$(dirname -- "$0")" || exit 1' + "`n" + 'exec "./' + $relativeExecutable + '" -- --' + $exportProfile.ToLowerInvariant() + ' "$@"' + "`n"
            $path = Join-Path $stageDirectory "Start-$exportProfile.sh"
            [IO.File]::WriteAllText($path, $launcher, [Text.UTF8Encoding]::new($false))
            if (-not $IsWindows) { & chmod +x $path; if ($LASTEXITCODE -ne 0) { throw 'Failed to mark launcher executable.' } }
        }
    }
    if ($macTarget -and $IsMacOS) {
        & codesign --force --deep --sign - $executable
        if ($LASTEXITCODE -ne 0) { throw 'Ad-hoc signing of the completed macOS app failed.' }
    }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputDirectory)) | Out-Null

    $hadPreviousBuild = Test-Path -LiteralPath $outputDirectory
    if ($hadPreviousBuild) {
        foreach ($userDirectory in @('Saves', 'Backups')) {
            $source = Join-Path $outputDirectory $userDirectory
            if (Test-Path -LiteralPath $source -PathType Container) {
                Copy-Item -LiteralPath $source -Destination (Join-Path $stageDirectory $userDirectory) -Recurse -Force
            }
        }
    }
    if ($hadPreviousBuild) { Move-Item -LiteralPath $outputDirectory -Destination $backupDirectory }
    try {
        Move-Item -LiteralPath $stageDirectory -Destination $outputDirectory
    }
    catch {
        if ($hadPreviousBuild -and -not (Test-Path -LiteralPath $outputDirectory)) {
            Move-Item -LiteralPath $backupDirectory -Destination $outputDirectory
        }
        throw
    }
    if ($hadPreviousBuild) {
        Assert-BuildChild $backupDirectory | Out-Null
        [IO.Directory]::Delete($backupDirectory, $true)
    }
    Write-Output "Godot $RuntimeIdentifier export complete: $outputDirectory"
}
finally {
    if ($createdSolution -and (Test-Path -LiteralPath $solution -PathType Leaf)) {
        Remove-Item -LiteralPath $solution
    }
    if (Test-Path -LiteralPath $stageDirectory) {
        Assert-BuildChild $stageDirectory | Out-Null
        [IO.Directory]::Delete($stageDirectory, $true)
    }
}
exit 0
