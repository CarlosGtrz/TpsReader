<#
.SYNOPSIS
Publishes a verified TpsReader Agent Skill tarball to npm.

.DESCRIPTION
Validates the source package, signed skill tag, npm tarball contents, and
SHA-256 sidecar before publishing the exact tarball. The script refuses to
publish an existing version and lets npm prompt interactively for 2FA so an
OTP is not placed in command history.

.PARAMETER TarballPath
Path to the npm tarball. When omitted, the script requires exactly one matching
tarball below artifacts/ for the package version in plugins/tpsreader/package.json.

.PARAMETER Registry
HTTPS npm registry URL. Defaults to the public npm registry.

.PARAMETER SkipLogin
Do not offer npm browser login when npm whoami fails.

.PARAMETER VerificationAttempts
Number of npm view attempts after publishing. Defaults to 24.

.PARAMETER VerificationDelaySeconds
Seconds between npm view attempts. Defaults to 5.

.EXAMPLE
./scripts/publish-agent-skill.ps1 -WhatIf

.EXAMPLE
./scripts/publish-agent-skill.ps1 -TarballPath ./artifacts/skill-v1.0.2-release/dist/carlosgtrz-tpsreader-agent-skill-1.0.2.tgz
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [string] $TarballPath,
    [string] $Registry = 'https://registry.npmjs.org/',
    [switch] $SkipLogin,
    [ValidateRange(1, 120)]
    [int] $VerificationAttempts = 24,
    [ValidateRange(1, 60)]
    [int] $VerificationDelaySeconds = 5
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$pluginRoot = Join-Path $repositoryRoot 'plugins/tpsreader'
$packagePath = Join-Path $pluginRoot 'package.json'
$validatorPath = Join-Path $PSScriptRoot 'validate-agent-package.ps1'
$expectedFiles = @(
    '.claude-plugin/plugin.json',
    '.codex-plugin/plugin.json',
    'LICENSE',
    'README.md',
    'package.json',
    'skills/read-tps-files/SKILL.md',
    'skills/read-tps-files/agents/openai.yaml',
    'skills/read-tps-files/references/cli-resolution.md'
) | Sort-Object

function Assert-Condition {
    param(
        [bool] $Condition,
        [string] $Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Read-JsonObject {
    param([string] $Path)

    Assert-Condition (Test-Path -LiteralPath $Path -PathType Leaf) "Missing JSON file: $Path"
    return Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
}

function Invoke-NpmCapture {
    param(
        [string[]] $Arguments,
        [switch] $AllowFailure
    )

    $previousPreference = $PSNativeCommandUseErrorActionPreference
    try {
        $PSNativeCommandUseErrorActionPreference = $false
        $output = (& npm @Arguments 2>&1) -join [Environment]::NewLine
        $exitCode = $LASTEXITCODE
    }
    finally {
        $PSNativeCommandUseErrorActionPreference = $previousPreference
    }

    if (-not $AllowFailure -and $exitCode -ne 0) {
        throw "npm $($Arguments[0]) failed with exit code ${exitCode}: $output"
    }

    return [pscustomobject]@{
        ExitCode = $exitCode
        Output = $output
    }
}

Assert-Condition ($null -ne (Get-Command npm -ErrorAction SilentlyContinue)) 'npm is required.'
Assert-Condition ($null -ne (Get-Command tar -ErrorAction SilentlyContinue)) 'tar is required to inspect the release archive.'
Assert-Condition ($null -ne (Get-Command git -ErrorAction SilentlyContinue)) 'git is required to verify the release tag.'
Assert-Condition (Test-Path -LiteralPath $validatorPath -PathType Leaf) "Missing package validator: $validatorPath"

$registryUri = $null
Assert-Condition ([Uri]::TryCreate($Registry, [UriKind]::Absolute, [ref] $registryUri)) "Invalid registry URL: $Registry"
Assert-Condition ($registryUri.Scheme -eq 'https') 'The npm registry URL must use HTTPS.'
$Registry = $registryUri.AbsoluteUri

$package = Read-JsonObject $packagePath
Assert-Condition ($package.name -eq '@carlosgtrz/tpsreader-agent-skill') "Unexpected package name: $($package.name)"
Assert-Condition ($package.version -match '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') "Invalid package version: $($package.version)"
Assert-Condition ($package.publishConfig.access -eq 'public') 'The package must declare public publish access.'

$inheritedWhatIfPreference = $WhatIfPreference
try {
    $WhatIfPreference = $false
    & $validatorPath -ExpectedVersion $package.version
}
finally {
    $WhatIfPreference = $inheritedWhatIfPreference
}

$packageChanges = (& git -C $repositoryRoot status --porcelain -- plugins/tpsreader) -join [Environment]::NewLine
Assert-Condition ($LASTEXITCODE -eq 0) 'Could not inspect the plugin working tree.'
Assert-Condition ([string]::IsNullOrWhiteSpace($packageChanges)) 'plugins/tpsreader has uncommitted changes. Publish only from committed package source.'

$releaseTag = "skill-v$($package.version)"
$tagType = (& git -C $repositoryRoot cat-file -t $releaseTag 2>$null) -join ''
Assert-Condition ($LASTEXITCODE -eq 0 -and $tagType -eq 'tag') "Missing annotated release tag: $releaseTag"
$tagObject = (& git -C $repositoryRoot cat-file -p $releaseTag) -join [Environment]::NewLine
Assert-Condition ($LASTEXITCODE -eq 0 -and $tagObject.Contains('BEGIN SSH SIGNATURE')) "Release tag is not SSH-signed: $releaseTag"
$tagCommit = ((& git -C $repositoryRoot rev-list -n 1 $releaseTag) -join '').Trim()
Assert-Condition ($LASTEXITCODE -eq 0 -and $tagCommit -match '^[0-9a-f]{40}$') "Could not resolve release tag: $releaseTag"

$tarballName = "$($package.name.TrimStart('@').Replace('/', '-'))-$($package.version).tgz"
if ([string]::IsNullOrWhiteSpace($TarballPath)) {
    $artifactRoot = Join-Path $repositoryRoot 'artifacts'
    Assert-Condition (Test-Path -LiteralPath $artifactRoot -PathType Container) "Artifact directory does not exist: $artifactRoot"
    $matches = @(
        Get-ChildItem -LiteralPath $artifactRoot -Recurse -File -Filter $tarballName |
            Where-Object {
                $_.FullName -match "[\\/]skill-v$([regex]::Escape($package.version))-release-[^\\/]+[\\/]dist[\\/]"
            }
    )
    Assert-Condition ($matches.Count -gt 0) "No release tarball named '$tarballName' was found below $artifactRoot."
    Assert-Condition ($matches.Count -eq 1) "Multiple release tarballs were found. Pass -TarballPath explicitly: $($matches.FullName -join ', ')"
    $TarballPath = $matches[0].FullName
}

$resolvedTarball = (Resolve-Path -LiteralPath $TarballPath).Path
Assert-Condition ((Split-Path $resolvedTarball -Leaf) -ceq $tarballName) "Tarball filename must be '$tarballName'."
$checksumPath = "$resolvedTarball.sha256"
Assert-Condition (Test-Path -LiteralPath $checksumPath -PathType Leaf) "Missing SHA-256 sidecar: $checksumPath"

$checksumText = (Get-Content -Raw -LiteralPath $checksumPath).Trim()
$checksumMatch = [regex]::Match($checksumText, '^(?<hash>[0-9a-fA-F]{64})\s+\*?(?<file>\S+)$')
Assert-Condition $checksumMatch.Success "Invalid SHA-256 sidecar format: $checksumPath"
Assert-Condition ($checksumMatch.Groups['file'].Value -ceq $tarballName) 'SHA-256 sidecar names a different tarball.'
$expectedDigest = $checksumMatch.Groups['hash'].Value.ToLowerInvariant()
$actualDigest = (Get-FileHash -Algorithm SHA256 -LiteralPath $resolvedTarball).Hash.ToLowerInvariant()
Assert-Condition ($actualDigest -eq $expectedDigest) "Tarball SHA-256 mismatch. Expected $expectedDigest, found $actualDigest."

$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$inspectionRoot = Join-Path $temporaryRoot "tpsreader-agent-publish-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $inspectionRoot -WhatIf:$false | Out-Null

try {
    & tar -xf $resolvedTarball -C $inspectionRoot
    Assert-Condition ($LASTEXITCODE -eq 0) 'Could not unpack the npm tarball.'

    $unpackedRoot = Join-Path $inspectionRoot 'package'
    Assert-Condition (Test-Path -LiteralPath $unpackedRoot -PathType Container) 'The tarball does not contain the expected package directory.'
    $unpackedFiles = @(
        Get-ChildItem -LiteralPath $unpackedRoot -Recurse -File -Force |
            ForEach-Object {
                [IO.Path]::GetRelativePath($unpackedRoot, $_.FullName).Replace('\', '/')
            } |
            Sort-Object
    )
    Assert-Condition ($unpackedFiles.Count -eq $expectedFiles.Count) "Tarball contains an unexpected number of files: $($unpackedFiles -join ', ')"
    for ($index = 0; $index -lt $expectedFiles.Count; $index++) {
        Assert-Condition ($unpackedFiles[$index] -eq $expectedFiles[$index]) "Tarball files differ. Expected '$($expectedFiles[$index])', found '$($unpackedFiles[$index])'."
    }

    $packedPackage = Read-JsonObject (Join-Path $unpackedRoot 'package.json')
    Assert-Condition ($packedPackage.name -eq $package.name) 'Tarball package name differs from source metadata.'
    Assert-Condition ($packedPackage.version -eq $package.version) 'Tarball version differs from source metadata.'
    Assert-Condition ($packedPackage.license -eq 'Apache-2.0') 'Tarball license must be Apache-2.0.'
    Assert-Condition ($packedPackage.repository.url -eq 'git+https://github.com/CarlosGtrz/TpsReader.git') 'Tarball repository URL is unexpected.'
    Assert-Condition ($packedPackage.pi.skills.Count -eq 1 -and $packedPackage.pi.skills[0] -eq './skills') 'Tarball Pi skill metadata is unexpected.'
}
finally {
    $resolvedInspectionRoot = [IO.Path]::GetFullPath($inspectionRoot)
    $expectedPrefix = $temporaryRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $isSafeTemporaryPath =
        $resolvedInspectionRoot.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path $resolvedInspectionRoot -Leaf).StartsWith('tpsreader-agent-publish-', [StringComparison]::Ordinal)
    Assert-Condition $isSafeTemporaryPath "Refusing to remove unexpected inspection directory: $resolvedInspectionRoot"
    Remove-Item -LiteralPath $resolvedInspectionRoot -Recurse -Force -WhatIf:$false
}

$packageSpec = "$($package.name)@$($package.version)"
$existing = Invoke-NpmCapture -Arguments @('view', $packageSpec, 'version', '--json', "--registry=$Registry") -AllowFailure
if ($existing.ExitCode -eq 0) {
    $publishedVersion = ($existing.Output | ConvertFrom-Json).ToString()
    if ($publishedVersion -eq $package.version) {
        throw "$packageSpec is already published to $Registry. npm versions are immutable."
    }
    throw "npm returned unexpected metadata for ${packageSpec}: $($existing.Output)"
}
Assert-Condition ($existing.Output -match '(?i)\bE404\b|404 Not Found') "Could not safely determine whether $packageSpec already exists: $($existing.Output)"

Write-Host ''
Write-Host "Package:      $packageSpec"
Write-Host "Release tag:  $releaseTag ($tagCommit)"
Write-Host "Tarball:      $resolvedTarball"
Write-Host "SHA-256:      $actualDigest"
Write-Host "Registry:     $Registry"
Write-Host ''

$target = "$packageSpec from '$resolvedTarball'"
if (-not $PSCmdlet.ShouldProcess($target, "Publish public package to $Registry")) {
    return
}

$identity = Invoke-NpmCapture -Arguments @('whoami', "--registry=$Registry") -AllowFailure
if ($identity.ExitCode -ne 0) {
    if ($SkipLogin) {
        throw "npm authentication is required and -SkipLogin was specified."
    }

    Write-Host 'npm authentication is required. Starting browser login...'
    & npm login --auth-type=web "--registry=$Registry"
    Assert-Condition ($LASTEXITCODE -eq 0) 'npm login failed.'
    $identity = Invoke-NpmCapture -Arguments @('whoami', "--registry=$Registry")
}

$npmUser = $identity.Output.Trim()
$scopeOwner = $package.name.Split('/')[0].TrimStart('@')
Assert-Condition ($npmUser.Equals($scopeOwner, [StringComparison]::OrdinalIgnoreCase)) "Authenticated npm user '$npmUser' does not own scope '@$scopeOwner'."

Write-Host "Authenticated as $npmUser. npm will prompt for 2FA when required."
& npm publish $resolvedTarball --access public "--registry=$Registry"
Assert-Condition ($LASTEXITCODE -eq 0) 'npm publish failed.'

$verified = $false
$publishedMetadata = $null
for ($attempt = 1; $attempt -le $VerificationAttempts; $attempt++) {
    $view = Invoke-NpmCapture -Arguments @('view', $packageSpec, '--json', "--registry=$Registry") -AllowFailure
    if ($view.ExitCode -eq 0) {
        try {
            $publishedMetadata = $view.Output | ConvertFrom-Json
            $verified =
                $publishedMetadata.name -eq $package.name -and
                $publishedMetadata.version -eq $package.version -and
                $publishedMetadata.license -eq 'Apache-2.0' -and
                $publishedMetadata.repository.url -eq 'git+https://github.com/CarlosGtrz/TpsReader.git' -and
                @($publishedMetadata.pi.skills).Count -eq 1 -and
                $publishedMetadata.pi.skills[0] -eq './skills'
        }
        catch {
            $verified = $false
        }
    }

    if ($verified) {
        break
    }

    if ($attempt -lt $VerificationAttempts) {
        Start-Sleep -Seconds $VerificationDelaySeconds
    }
}

Assert-Condition $verified "npm accepted the publish, but $packageSpec did not expose the expected metadata after $VerificationAttempts attempts."
Write-Host ''
Write-Host "Published and verified $packageSpec."
Write-Host "Install with: pi install npm:$packageSpec"
