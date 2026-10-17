[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,

    [string]$EvidenceDirectory,

    [switch]$KeepWorkspace
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.IO.Compression.FileSystem

$scriptRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptRoot '..'))
$templateRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'samples/PackageConsumer'))
$packageFullPath = [System.IO.Path]::GetFullPath($PackagePath)
$packageId = $null
$packageVersion = $null
$packageSha256 = $null
$libraryDllSha256 = $null
$loadedLibraryDllSha256 = $null
$cachedPackageSha256 = $null
$consumerChecks = @()
$workspaceRoot = $null
$tempDllPath = $null
$tempDllOwned = $false
$workspaceToken = [guid]::NewGuid().ToString('N')
$failureStage = $null
$failureType = $null
$commandChecks = [ordered]@{
    packageMetadata = $false
    isolatedWorkspace = $false
    exactLocalFeedCopy = $false
    restore = $false
    build = $false
    realPostgreSqlConsumer = $false
    freshPackageCacheHash = $false
    loadedLibraryHash = $false
    workspaceCleanup = 'pending'
}

if (-not (Test-Path -LiteralPath $packageFullPath -PathType Leaf)) {
    throw 'The supplied package file does not exist.'
}
if (-not (Test-Path -LiteralPath $templateRoot -PathType Container)) {
    throw 'The tracked package-consumer template is missing.'
}

$evidenceFullPath = $null
if (-not [string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
    $evidenceFullPath = [System.IO.Path]::GetFullPath($EvidenceDirectory)
    [System.IO.Directory]::CreateDirectory($evidenceFullPath) | Out-Null
}

function Get-Sha256Hex([string]$Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        return [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
    }
    finally {
        $stream.Dispose()
    }
}

function Get-PathComparison {
    if ([System.OperatingSystem]::IsWindows()) {
        return [System.StringComparison]::OrdinalIgnoreCase
    }
    return [System.StringComparison]::Ordinal
}

function Test-IsWithinPath([string]$Candidate, [string]$Parent) {
    $comparison = Get-PathComparison
    $candidateFull = [System.IO.Path]::GetFullPath($Candidate).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $parentFull = [System.IO.Path]::GetFullPath($Parent).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    return $candidateFull.StartsWith($parentFull + [System.IO.Path]::DirectorySeparatorChar, $comparison)
}

function Invoke-Dotnet([string[]]$Arguments, [string]$WorkingDirectory, [string]$PackagesDirectory, [string]$ExpectedVersion) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'dotnet'
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    $startInfo.Environment['NUGET_PACKAGES'] = $PackagesDirectory
    $startInfo.Environment['PGEXT_EXPECTED_PACKAGE_VERSION'] = $ExpectedVersion
    foreach ($argument in $Arguments) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw 'dotnet could not be started.'
    }

    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    $exitCode = $process.ExitCode
    $process.Dispose()
    if ($evidenceFullPath) {
        $stageName = $Arguments[0]
        [System.IO.File]::WriteAllText((Join-Path $evidenceFullPath "$stageName.stdout.log"), $stdout)
        [System.IO.File]::WriteAllText((Join-Path $evidenceFullPath "$stageName.stderr.log"), $stderr)
    }
    return [pscustomobject]@{
        ExitCode = $exitCode
        StdOut = $stdout
        StdErr = $stderr
    }
}

function Write-NeutralEvidence {
    param(
        [string]$Status,
        [string]$EvidencePath,
        [string]$ProjectFile,
        [System.Collections.IDictionary]$Checks
    )

    $payload = [ordered]@{
        status = $Status
        packageId = $packageId
        packageVersion = $packageVersion
        packageSha256 = $packageSha256
        cachedPackageSha256 = $cachedPackageSha256
        packageLibraryDllSha256 = $libraryDllSha256
        loadedLibraryDllSha256 = $loadedLibraryDllSha256
        consumerChecks = $consumerChecks
        checks = $Checks
        failureStage = $failureStage
        failureType = $failureType
    }
    $json = $payload | ConvertTo-Json -Depth 8
    if ($EvidencePath) {
        [System.IO.File]::WriteAllText((Join-Path $EvidencePath 'package-consumer-result.json'), $json + [Environment]::NewLine)
        if ($ProjectFile -and (Test-Path -LiteralPath $ProjectFile -PathType Leaf)) {
            Copy-Item -LiteralPath $ProjectFile -Destination (Join-Path $EvidencePath 'PackageConsumer.csproj') -Force
        }
    }
    Write-Output $json
}

$status = 'failed'
$consumerProject = $null
try {
    $packageSha256 = Get-Sha256Hex $packageFullPath
    $archive = [System.IO.Compression.ZipFile]::OpenRead($packageFullPath)
    try {
        $nuspecEntry = $archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [System.StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
        if ($null -eq $nuspecEntry) {
            throw 'The supplied package has no nuspec.'
        }

        $nuspecReader = [System.IO.StreamReader]::new($nuspecEntry.Open())
        try {
            [xml]$nuspec = $nuspecReader.ReadToEnd()
        }
        finally {
            $nuspecReader.Dispose()
        }

        $packageId = [string]$nuspec.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='id']").InnerText
        $packageVersion = [string]$nuspec.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='version']").InnerText
        if ($packageId -cne 'EntityFrameworkCore.PostgreSQL.Extensions') {
            throw 'The supplied package ID does not match the approved library package.'
        }
        if ($packageVersion -notmatch '^[0-9A-Za-z][0-9A-Za-z.+-]*$') {
            throw 'The package version is not a safe NuGet version token.'
        }

        $dllEntry = $archive.GetEntry('lib/net10.0/EntityFrameworkCore.PostgreSQL.Extensions.dll')
        if ($null -eq $dllEntry) {
            throw 'The package does not contain the required net10.0 library assembly.'
        }
        $tempDllPath = Join-Path ([System.IO.Path]::GetTempPath()) ("pgext-package-dll-{0}.dll" -f $workspaceToken)
        $entryStream = $dllEntry.Open()
        $dllFile = [System.IO.File]::Open($tempDllPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        $tempDllOwned = $true
        try {
            $entryStream.CopyTo($dllFile)
        }
        finally {
            $entryStream.Dispose()
            $dllFile.Dispose()
        }
        $libraryDllSha256 = Get-Sha256Hex $tempDllPath
        Remove-Item -LiteralPath $tempDllPath -Force
        $tempDllPath = $null
        $tempDllOwned = $false
    }
    finally {
        $archive.Dispose()
    }
    $commandChecks.packageMetadata = $true

    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $workspaceRoot = [System.IO.Path]::GetFullPath((Join-Path $tempRoot ("pgext-package-consumer-{0}" -f $workspaceToken)))
    if (-not [string]::Equals([System.IO.Path]::GetDirectoryName($workspaceRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar), $tempRoot, (Get-PathComparison))) {
        throw 'The isolated workspace is not a direct child of the operating-system temp directory.'
    }
    if ((Test-IsWithinPath $workspaceRoot $repositoryRoot) -or (Test-IsWithinPath $repositoryRoot $workspaceRoot)) {
        throw 'The package-consumer workspace must remain outside the checkout.'
    }
    [System.IO.Directory]::CreateDirectory($workspaceRoot) | Out-Null
    $markerPath = Join-Path $workspaceRoot '.pgext-package-consumer-owner'
    [System.IO.File]::WriteAllText($markerPath, $workspaceToken)
    $commandChecks.isolatedWorkspace = $true

    $consumerRoot = Join-Path $workspaceRoot 'consumer'
    $feedRoot = Join-Path $workspaceRoot 'localfeed'
    $packagesRoot = Join-Path $workspaceRoot 'packages'
    [System.IO.Directory]::CreateDirectory($consumerRoot) | Out-Null
    [System.IO.Directory]::CreateDirectory($feedRoot) | Out-Null
    [System.IO.Directory]::CreateDirectory($packagesRoot) | Out-Null

    Copy-Item -Path (Join-Path $templateRoot '*') -Destination $consumerRoot -Recurse -Force
    $consumerProject = Join-Path $consumerRoot 'PackageConsumer.csproj'
    $projectText = [System.IO.File]::ReadAllText($consumerProject)
    if ([regex]::Matches($projectText, '__PACKAGE_VERSION__').Count -ne 1) {
        throw 'The tracked consumer project must contain exactly one package-version token.'
    }
    $projectText = $projectText.Replace('__PACKAGE_VERSION__', $packageVersion)
    [System.IO.File]::WriteAllText($consumerProject, $projectText)
    if ($projectText -match '<ProjectReference\b|Directory\.Build\.(props|targets)') {
        throw 'The isolated consumer project must not use source references or checkout build properties.'
    }

    $packageFileName = "$packageId.$packageVersion.nupkg"
    $feedPackagePath = Join-Path $feedRoot $packageFileName
    Copy-Item -LiteralPath $packageFullPath -Destination $feedPackagePath
    if ((Get-Sha256Hex $feedPackagePath) -cne $packageSha256) {
        throw 'The local-feed package copy does not match the supplied package bytes.'
    }

    $configTemplatePath = Join-Path $consumerRoot 'NuGet.config.template'
    $configTemplate = [System.IO.File]::ReadAllText($configTemplatePath)
    if ([regex]::Matches($configTemplate, '__LOCAL_FEED__').Count -ne 1) {
        throw 'The tracked NuGet config template must contain exactly one local-feed token.'
    }
    $escapedFeedRoot = [System.Security.SecurityElement]::Escape($feedRoot)
    $nugetConfigPath = Join-Path $consumerRoot 'NuGet.config'
    [System.IO.File]::WriteAllText($nugetConfigPath, $configTemplate.Replace('__LOCAL_FEED__', $escapedFeedRoot))
    Remove-Item -LiteralPath $configTemplatePath -Force
    $commandChecks.exactLocalFeedCopy = $true

    $restore = Invoke-Dotnet -Arguments @('restore', $consumerProject, '--configfile', $nugetConfigPath, '--packages', $packagesRoot, '--force', '--no-cache') -WorkingDirectory $consumerRoot -PackagesDirectory $packagesRoot -ExpectedVersion $packageVersion
    if ($restore.ExitCode -ne 0) {
        $failureStage = 'restore'
        throw 'The isolated consumer restore failed.'
    }
    $commandChecks.restore = $true

    $build = Invoke-Dotnet -Arguments @('build', $consumerProject, '--configuration', 'Release', '--no-restore') -WorkingDirectory $consumerRoot -PackagesDirectory $packagesRoot -ExpectedVersion $packageVersion
    if ($build.ExitCode -ne 0) {
        $failureStage = 'build'
        throw 'The isolated consumer build failed.'
    }
    $commandChecks.build = $true

    $run = Invoke-Dotnet -Arguments @('run', '--project', $consumerProject, '--configuration', 'Release', '--no-build', '--no-restore') -WorkingDirectory $consumerRoot -PackagesDirectory $packagesRoot -ExpectedVersion $packageVersion
    $resultLine = $run.StdOut -split "`r?`n" | Where-Object { $_.StartsWith('PGEXT_PACKAGE_CONSUMER_RESULT:', [System.StringComparison]::Ordinal) } | Select-Object -Last 1
    if (-not $resultLine) {
        $failureStage = 'run'
        throw 'The real PostgreSQL package-consumer process failed.'
    }
    $consumerResult = $resultLine.Substring('PGEXT_PACKAGE_CONSUMER_RESULT:'.Length) | ConvertFrom-Json
    $consumerChecks = @($consumerResult.checks)
    if ($run.ExitCode -ne 0 -or $consumerResult.status -cne 'passed') {
        $failureStage = 'run'
        $failureType = [string]$consumerResult.failureType
        throw 'The package-consumer executable reported a failed check.'
    }
    $commandChecks.realPostgreSqlConsumer = $true

    $cachePackagePath = Join-Path (Join-Path (Join-Path $packagesRoot $packageId.ToLowerInvariant()) $packageVersion.ToLowerInvariant()) "$($packageId.ToLowerInvariant()).$($packageVersion.ToLowerInvariant()).nupkg"
    $cachedPackageSha256 = if (Test-Path -LiteralPath $cachePackagePath -PathType Leaf) { Get-Sha256Hex $cachePackagePath } else { $null }
    if ((-not (Test-Path -LiteralPath $cachePackagePath -PathType Leaf)) -or ($cachedPackageSha256 -cne $packageSha256)) {
        $failureStage = 'package-cache-hash'
        throw 'The fresh NuGet package cache does not contain the supplied package bytes.'
    }
    $commandChecks.freshPackageCacheHash = $true

    $loadedLibraryDllSha256 = [string]$consumerResult.loadedAssemblySha256
    if ((-not [bool]$consumerResult.loadedFromExpectedPackage) -or ($loadedLibraryDllSha256 -cne $libraryDllSha256)) {
        $failureStage = 'loaded-assembly-hash'
        throw 'The runtime-loaded library assembly does not match the exact package assembly.'
    }
    $commandChecks.loadedLibraryHash = $true
    $status = 'passed'
}
catch {
    if (-not $failureStage) {
        $failureStage = 'preflight-or-package-inspection'
    }
    if (-not $failureType) {
        $failureType = $_.Exception.GetType().Name
    }
}
finally {
    if ($tempDllOwned -and $tempDllPath -and (Test-Path -LiteralPath $tempDllPath -PathType Leaf)) {
        try {
            Remove-Item -LiteralPath ([System.IO.Path]::GetFullPath($tempDllPath)) -Force
            $tempDllPath = $null
            $tempDllOwned = $false
        }
        catch {
            if ($status -eq 'passed') {
                $status = 'failed'
                $failureStage = 'package-inspection-cleanup'
                $failureType = $_.Exception.GetType().Name
            }
        }
    }

    if ($workspaceRoot) {
        if ($evidenceFullPath -and $consumerProject -and (Test-Path -LiteralPath $consumerProject -PathType Leaf)) {
            Copy-Item -LiteralPath $consumerProject -Destination (Join-Path $evidenceFullPath 'PackageConsumer.csproj') -Force
        }

        if ($KeepWorkspace) {
            $commandChecks.workspaceCleanup = 'retained-by-request'
        }
        else {
            try {
                $checkedRoot = [System.IO.Path]::GetFullPath($workspaceRoot)
                $expectedTempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
                $parentRoot = [System.IO.Path]::GetDirectoryName($checkedRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
                $rootItem = Get-Item -LiteralPath $checkedRoot -Force
                $markerPath = Join-Path $checkedRoot '.pgext-package-consumer-owner'
                $markerValue = if (Test-Path -LiteralPath $markerPath -PathType Leaf) { [System.IO.File]::ReadAllText($markerPath) } else { '' }
                $rootName = [System.IO.Path]::GetFileName($checkedRoot)
                $parentMatchesTemp = [string]::Equals($parentRoot, $expectedTempRoot, (Get-PathComparison))
                $expectedName = "pgext-package-consumer-$workspaceToken"
                $isReparsePoint = ($rootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
                $rootIsInsideCheckout = Test-IsWithinPath $checkedRoot $repositoryRoot
                $workspaceLooksSafe = $parentMatchesTemp `
                    -and ($rootName -ceq $expectedName) `
                    -and (-not $isReparsePoint) `
                    -and ($markerValue -ceq $workspaceToken) `
                    -and (-not $rootIsInsideCheckout)
                if (-not $workspaceLooksSafe) {
                    throw 'Owned temporary workspace checks failed; recursive cleanup was withheld.'
                }
                Remove-Item -LiteralPath $checkedRoot -Recurse -Force
                $commandChecks.workspaceCleanup = 'removed'
            }
            catch {
                $commandChecks.workspaceCleanup = 'failed-safe-check'
                if ($status -eq 'passed') {
                    $status = 'failed'
                    $failureStage = 'workspace-cleanup'
                    $failureType = $_.Exception.GetType().Name
                }
            }
        }
    }

    Write-NeutralEvidence -Status $status -EvidencePath $evidenceFullPath -ProjectFile $consumerProject -Checks $commandChecks
}

if ($status -ne 'passed') {
    exit 1
}
