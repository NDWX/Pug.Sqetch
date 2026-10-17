#!/usr/bin/env pwsh
# The one entry point for building, packaging and smoke-testing a Sqetch CLI release. The GitHub
# workflows call nothing else, and the same commands work on a developer's machine.
#
#   build.ps1 -Stage identify  [-Ref refs/tags/sqetch-pre-v0.1.0 | -Product sqetch]
#   build.ps1 -Stage test
#   build.ps1 -Stage publish   -Product sqetch -Version 0.1.0 -Rid linux-x64,linux-arm64 [-Sign]
#   build.ps1 -Stage package   -Product sqetch -Version 0.1.0 -Rid linux-x64 [-Sign]
#   build.ps1 -Stage smoke     -Product sqetch -Version 0.1.0 [-Rid <host rid>]
#   build.ps1 -Stage pack-tool | smoke-tool | checksums -Product sqetch -Version 0.1.0
#   build.ps1 -Stage push-tool -Product sqetch -Version 0.1.0 [-DryRun]   (NUGET_API_KEY in the environment)
#   build.ps1 -Stage release   -Product sqetch -Version 0.1.0 -Tag sqetch-v0.1.0 [-Prerelease] [-Title <text>] [-DryRun]
#
# Without -Version a stage builds <project version>-dev.<UTC stamp>. Artifacts land in
# <repo>/artifacts (publish/, dist/, work/) unless -ArtifactsDir says otherwise; dist/ holds exactly
# what a release uploads.
#
# The release stage creates a draft GitHub release for -Tag holding exactly what is in dist/ (run
# checksums first so SHA256SUMS is among it), through the gh CLI with GH_TOKEN set. A draft that
# already exists is brought in line with dist/, its assets replaced; a published release is never
# touched, because its assets are immutable. -DryRun prints what it would do and calls nothing.
#
# The push-tool stage pushes the tool package pack-tool left in dist/ to nuget.org. That cannot be
# undone (a version can only be unlisted), so the release workflow runs it only after every smoke
# test passed. -DryRun prints the push without making it.

[CmdletBinding()]
param(
	[Parameter( Mandatory )]
	[ValidateSet( 'identify', 'test', 'publish', 'package', 'smoke', 'pack-tool', 'smoke-tool', 'push-tool', 'checksums', 'release' )]
	[string] $Stage,

	[ValidateSet( 'sqetch', 'sqetch-deploy' )]
	[string] $Product,

	# the git ref being built; identify reads the product and version from a release tag
	[string] $Ref = $env:GITHUB_REF,

	[string] $Version,

	[string[]] $Rid,

	# release stage: the tag to release, whether it is a pre-release, and the release title
	# (default '<product> <version>')
	[string] $Tag,

	[switch] $Prerelease,

	[string] $Title,

	# release and push-tool stages: print the gh or dotnet nuget commands instead of running them
	[switch] $DryRun,

	[string] $ArtifactsDir,

	# publish and package stages: sign Windows executables and installers (Azure Key Vault; see
	# Windows.ps1); release stage: the build was signed, so the notes carry no unsigned-build warning
	[switch] $Sign,

	# run docker through 'sg docker -c', for a shell started before joining the docker group
	[switch] $DockerViaSg
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. ( Join-Path $PSScriptRoot 'Common.ps1' )

if( $IsWindows ) { . ( Join-Path $PSScriptRoot 'Windows.ps1' ) }
elseif( $IsMacOS ) { . ( Join-Path $PSScriptRoot 'MacOS.ps1' ) }
elseif( $IsLinux ) { . ( Join-Path $PSScriptRoot 'Linux.ps1' ) }
else { throw 'unsupported platform' }

$script:DockerViaSg = [bool] $DockerViaSg

# Runs gh and returns its stdout, or just prints the command under -DryRun.
function Invoke-GitHub
{
	param( [Parameter( Mandatory )] [string[]] $Arguments )

	if( $DryRun )
	{
		Write-Host "[dry run] gh $( $Arguments -join ' ' )"
		return $null
	}

	return Invoke-Native -FilePath 'gh' -Arguments $Arguments -Capture
}

# Creates the draft release for -Tag from dist/, or replaces the assets of the draft a previous
# run left. A published release is an error: its assets cannot be replaced.
function Publish-GitHubRelease
{
	param( [Parameter( Mandatory )] $Context )

	if( -not $Tag )
	{
		throw 'stage release needs -Tag'
	}

	if( $Tag -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$' )
	{
		throw "'$Tag' is not a usable tag name"
	}

	$files = @( Get-ChildItem -File -Path $Context.DistDir -ErrorAction SilentlyContinue | Sort-Object Name )

	if( -not $files )
	{
		throw "nothing to release: $( $Context.DistDir ) is empty"
	}

	if( $files.Name -notcontains 'SHA256SUMS' )
	{
		throw "$( $Context.DistDir ) has no SHA256SUMS; run -Stage checksums first"
	}

	$title = $Title ? $Title : "$( $Context.Name ) $( $Context.Version )"
	$notes = "$title. Verify downloads against SHA256SUMS."

	if( -not $Context.Sign )
	{
		$notes += "`n`nThe Windows executables and installers are not code-signed, so Windows SmartScreen may warn before running them."
	}

	$paths = @( $files | ForEach-Object FullName )

	# the releases API lists drafts, which a lookup by tag does not reliably find; the tag is
	# validated above, so it is safe inside the jq program
	$listing = Invoke-GitHub -Arguments @(
		'api', '--paginate', 'repos/{owner}/{repo}/releases?per_page=100', '--jq',
		".[] | select( .tag_name == `"$Tag`" ) | { draft, assets: [ .assets[].name ] } | tojson" )

	$found = @( "$listing" -split "`n" | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json } )

	if( $found | Where-Object { -not $_.draft } )
	{
		throw "release $Tag is already published and its assets are immutable; delete the release (not the tag) or release a new version"
	}

	if( -not $found )
	{
		Write-Host "creating draft release $Tag '$title'$( $Prerelease ? ' (pre-release)' : '' )"

		$null = Invoke-GitHub -Arguments ( @(
			'release', 'create', $Tag, '--draft', '--verify-tag', '--title', $title,
			'--notes', $notes ) +
			( $Prerelease ? @( '--prerelease' ) : @() ) + $paths )

		return
	}

	Write-Host "draft release $Tag exists; replacing its assets"

	$null = Invoke-GitHub -Arguments ( @(
		'release', 'edit', $Tag, '--title', $title, '--notes', $notes,
		"--prerelease=$( $Prerelease ? 'true' : 'false' )" ) )

	# a re-run's pre-release version carries a new stamp, so its file names differ from the draft's
	# (a draft without assets has none to enumerate, which StrictMode would otherwise reject)
	foreach( $asset in @( $found | ForEach-Object { $_.assets } | Where-Object { $_ -and $files.Name -notcontains $_ } ) )
	{
		$null = Invoke-GitHub -Arguments @( 'release', 'delete-asset', $Tag, $asset, '--yes' )
	}

	$null = Invoke-GitHub -Arguments ( @( 'release', 'upload', $Tag, '--clobber' ) + $paths )
}

if( $Stage -eq 'identify' )
{
	$identity = Resolve-BuildIdentity -Ref $Ref -ProductName $Product

	Write-StageOutputs ( [ordered]@{
		product      = $identity.Product
		project      = $identity.Project
		base_version = $identity.BaseVersion
		version      = $identity.Version
		signed       = $identity.Signed
		prerelease   = $identity.Prerelease
		release      = $identity.Release
		tag          = $identity.Tag
	} )

	return
}

if( $Stage -eq 'test' )
{
	Invoke-Native -FilePath 'dotnet' -Arguments @(
		'test', ( Join-Path $script:RepoRoot 'Pug.Sqetch.sln' ), '-m:1', '-nologo', '-c', 'Release' )

	return
}

if( -not $Product )
{
	throw "stage '$Stage' needs -Product"
}

if( -not $Version )
{
	$Version = "$( Get-ProjectVersion ( Get-Product $Product ) )-dev.$( Get-BuildStamp )"
}

$context = New-BuildContext -ProductName $Product -Version $Version -ArtifactsDir $ArtifactsDir -Sign:$Sign
$rids = $Rid ? @( $Rid | ForEach-Object { $_ -split ',' } | Where-Object { $_ } ) : @( Get-HostRid )

Write-Host "$Stage $( $context.Name ) $( $context.Version ) [$( $rids -join ', ' )]$( $context.Sign ? ' signed' : '' )"

switch( $Stage )
{
	'publish'
	{
		foreach( $r in $rids )
		{
			$output = Publish-Product -Context $context -Rid $r

			if( $context.Sign )
			{
				Invoke-PlatformSigning -Context $context -Files @( Join-Path $output ( Get-ExecutableName -Context $context -Rid $r ) )
			}
		}
	}

	'package'
	{
		foreach( $r in $rids )
		{
			$null = New-Archive -Context $context -Rid $r
			New-PlatformPackages -Context $context -Rid $r
		}
	}

	'smoke'
	{
		foreach( $r in $rids )
		{
			Test-Archive -Context $context -Rid $r
			Test-Installers -Context $context -Rid $r
		}
	}

	'pack-tool'
	{
		$null = New-ToolPackage -Context $context
	}

	'smoke-tool'
	{
		Test-ToolPackage -Context $context
	}

	'push-tool'
	{
		Publish-ToolPackage -Context $context -DryRun:$DryRun
	}

	'checksums'
	{
		$null = Write-Checksums -Context $context
	}

	'release'
	{
		Publish-GitHubRelease -Context $context
	}
}

Get-ChildItem -File -Path $context.DistDir -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  dist/$( $_.Name )" }
