# Platform-neutral release logic, dot-sourced by build.ps1 before exactly one of Windows.ps1,
# Linux.ps1 or MacOS.ps1. Each platform file defines the same three functions, so the stages in
# build.ps1 never branch on the platform themselves:
#
#   Invoke-PlatformSigning -Context <ctx> -Files <string[]>   sign in place, or throw where unsupported
#   New-PlatformPackages   -Context <ctx> -Rid <rid>           write installers into $Context.DistDir
#   Test-Installers        -Context <ctx> -Rid <rid>           install each, run Test-ProductCommands

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:RepoRoot = ( Resolve-Path ( Join-Path $PSScriptRoot '../..' ) ).Path

# Product name is what the binary, archives and packages are called; TagName is the tag prefix,
# kept distinct so 'sqetch-v*' can never match a 'sqetch-deploy' tag.
$script:Products = [ordered]@{
	'sqetch' = [pscustomobject]@{
		Name        = 'sqetch'
		TagName     = 'sqetch'
		Project     = 'Pug.Sqetch.Cli/Pug.Sqetch.Cli.csproj'
		PackageId   = 'Pug.Sqetch.Cli'
		Description = 'Sqetch: author database schema changes as plans and group them into releases'
	}
	'sqetch-deploy' = [pscustomobject]@{
		Name        = 'sqetch-deploy'
		TagName     = 'sqetch_deploy'
		Project     = 'Pug.Sqetch.Deployment.Cli/Pug.Sqetch.Deployment.Cli.csproj'
		PackageId   = 'Pug.Sqetch.Deployment.Cli'
		Description = 'Sqetch deploy: bundle Sqetch projects and deploy the bundles to a database through pluggable database drivers'
	}
}

# <product>-v1.2.3 is a signed release; <product>-pre-v1.2.3 an unsigned pre-release
$script:TagPattern = '^(?<product>sqetch|sqetch_deploy)(?<pre>-pre)?-v(?<version>\d+\.\d+\.\d+)$'

# the variables a signed build needs; identify fails fast on a release tag when any is missing
$script:SigningVariables = @( 'AZURE_CLIENT_ID', 'AZURE_TENANT_ID', 'SIGN_KEY_VAULT_URL', 'SIGN_CERTIFICATE_NAME' )

# the variables every tag build needs to publish (the nuget.org profile trusted publishing logs in
# as); identify fails fast on any tag, release or pre-release, when one is missing
$script:PublishingVariables = @( 'NUGET_USER' )

$script:NuGetSource = 'https://api.nuget.org/v3/index.json'

$script:DockerViaSg = $false

# Runs a native command and throws on a non-zero exit code. -Capture returns stdout as one string.
# -PassThru instead returns the exit code and the combined output as a product invoker's result
# ([pscustomobject]@{ ExitCode; Output }) and never throws on a non-zero code; the caller decides.
# -Secret values are shown as *** where the command is echoed.
function Invoke-Native
{
	param(
		[Parameter( Mandatory )] [string] $FilePath,
		[string[]] $Arguments = @(),
		[switch] $Capture,
		[switch] $PassThru,
		[string[]] $Secret = @()
	)

	if( $PassThru )
	{
		$output = & $FilePath @Arguments 2>&1 | Out-String

		return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
	}

	Write-Host "> $FilePath $( ( $Arguments | ForEach-Object { $Secret -contains $_ ? '***' : $_ } ) -join ' ' )"

	if( $Capture )
	{
		$output = & $FilePath @Arguments | Out-String
	}
	else
	{
		& $FilePath @Arguments | Out-Host
	}

	if( $LASTEXITCODE -ne 0 )
	{
		throw "$FilePath exited with code $LASTEXITCODE"
	}

	if( $Capture )
	{
		return $output.TrimEnd()
	}
}

# Docker, or 'sg docker -c' where the shell predates the docker group membership (local runs).
function Invoke-Docker
{
	param( [Parameter( Mandatory )] [string[]] $Arguments, [switch] $Capture, [switch] $PassThru )

	if( -not $script:DockerViaSg )
	{
		return Invoke-Native -FilePath 'docker' -Arguments $Arguments -Capture:$Capture -PassThru:$PassThru
	}

	$quoted = ( @( 'docker' ) + $Arguments | ForEach-Object { "'" + ( $_ -replace "'", "'\''" ) + "'" } ) -join ' '

	return Invoke-Native -FilePath 'sg' -Arguments @( 'docker', '-c', $quoted ) -Capture:$Capture -PassThru:$PassThru
}

function Get-Product
{
	param( [Parameter( Mandatory )] [string] $Name )

	$product = $script:Products[$Name]

	if( -not $product )
	{
		throw "unknown product '$Name'; expected one of: $( $script:Products.Keys -join ', ' )"
	}

	return $product
}

function Get-ProjectVersion
{
	param( [Parameter( Mandatory )] $Product )

	[xml] $project = Get-Content -Raw ( Join-Path $script:RepoRoot $Product.Project )
	$version = @( $project.Project.PropertyGroup | ForEach-Object { $_.SelectSingleNode( 'Version' ) } | Where-Object { $_ } )

	if( $version.Count -ne 1 )
	{
		throw "$( $Product.Project ) must declare exactly one <Version>"
	}

	return $version[0].InnerText.Trim()
}

# UTC, 24-hour, so stamps sort in build order. Computed once, in identify.
function Get-BuildStamp
{
	return [DateTime]::UtcNow.ToString( 'yyyyMMddHHmm', [Globalization.CultureInfo]::InvariantCulture )
}

# What a ref builds. A tag decides the product and version; any other ref (a branch, through
# workflow_dispatch, or a local run) builds -Product as an unsigned X.Y.Z-dev.<stamp>.
function Resolve-BuildIdentity
{
	param( [string] $Ref, [string] $ProductName )

	$tag = $null
	if( $Ref -like 'refs/tags/*' )
	{
		$tag = $Ref.Substring( 'refs/tags/'.Length )
	}

	if( $tag )
	{
		$match = [regex]::Match( $tag, $script:TagPattern )

		if( -not $match.Success )
		{
			throw "tag '$tag' is not <product>-v<X.Y.Z> or <product>-pre-v<X.Y.Z> (products: sqetch, sqetch_deploy)"
		}

		$product = $script:Products.Values | Where-Object TagName -eq $match.Groups['product'].Value
		$baseVersion = $match.Groups['version'].Value
		$prerelease = $match.Groups['pre'].Success
		$projectVersion = Get-ProjectVersion $product

		if( $baseVersion -ne $projectVersion )
		{
			throw "tag '$tag' names version $baseVersion but $( $product.Project ) declares $projectVersion"
		}

		$version = $prerelease ? "$baseVersion-pre.$( Get-BuildStamp )" : $baseVersion
		$signed = -not $prerelease
	}
	else
	{
		if( -not $ProductName )
		{
			throw 'a build that is not from a release tag needs -Product'
		}

		$product = Get-Product $ProductName
		$baseVersion = Get-ProjectVersion $product
		$version = "$baseVersion-dev.$( Get-BuildStamp )"
		$prerelease = $true
		$signed = $false
	}

	if( $signed )
	{
		$missing = @( $script:SigningVariables | Where-Object { -not [Environment]::GetEnvironmentVariable( $_ ) } )

		if( $missing )
		{
			throw "release tag '$tag' must be signed, but these signing variables are not set: $( $missing -join ', ' ). Use a <product>-pre-v<X.Y.Z> tag for an unsigned pre-release."
		}
	}

	if( $tag )
	{
		$missing = @( $script:PublishingVariables | Where-Object { -not [Environment]::GetEnvironmentVariable( $_ ) } )

		if( $missing )
		{
			throw "tag '$tag' publishes to nuget.org, but these variables are not set: $( $missing -join ', ' )"
		}
	}

	return [pscustomobject]@{
		Product     = $product.Name
		Project     = $product.Project
		BaseVersion = $baseVersion
		Version     = $version
		Signed      = $signed
		Prerelease  = $prerelease
		Release     = [bool] $tag
		Tag         = $tag ?? ''
	}
}

# Everything a stage needs about one product build, including where its files go.
function New-BuildContext
{
	param(
		[Parameter( Mandatory )] [string] $ProductName,
		[Parameter( Mandatory )] [string] $Version,
		[string] $ArtifactsDir,
		[switch] $Sign
	)

	$product = Get-Product $ProductName
	$artifacts = $ArtifactsDir ? [IO.Path]::GetFullPath( $ArtifactsDir ) : ( Join-Path $script:RepoRoot 'artifacts' )

	return [pscustomobject]@{
		Product      = $product
		Name         = $product.Name
		Version      = $Version
		BaseVersion  = ( $Version -split '-', 2 )[0]
		Sign         = [bool] $Sign
		RepoRoot     = $script:RepoRoot
		ProjectPath  = Join-Path $script:RepoRoot $product.Project
		ArtifactsDir = $artifacts
		PublishRoot  = Join-Path $artifacts 'publish'
		DistDir      = Join-Path $artifacts 'dist'
		WorkDir      = Join-Path $artifacts 'work'
	}
}

function Get-PublishDir
{
	param( [Parameter( Mandatory )] $Context, [Parameter( Mandatory )] [string] $Rid )

	return Join-Path $Context.PublishRoot "$( $Context.Name )-$Rid"
}

# The RID of the machine running the script; smoke tests run only what it can execute.
function Get-HostRid
{
	$arch = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
	$os = $IsWindows ? 'win' : $IsMacOS ? 'osx' : 'linux'

	return "$os-$arch"
}

function Get-ExecutableName
{
	param( [Parameter( Mandatory )] $Context, [Parameter( Mandatory )] [string] $Rid )

	return $Rid -like 'win-*' ? "$( $Context.Name ).exe" : $Context.Name
}

function Get-NuGetPackagesRoot
{
	if( $env:NUGET_PACKAGES )
	{
		return $env:NUGET_PACKAGES
	}

	$line = Invoke-Native -FilePath 'dotnet' -Arguments @( 'nuget', 'locals', 'global-packages', '--list' ) -Capture

	return ( $line -replace '^\s*global-packages:\s*', '' ).Trim()
}

# The licence of the .NET runtime a self-contained publish embeds, from the runtime pack it used.
function Get-RuntimePackDirectory
{
	param( [Parameter( Mandatory )] $Context, [Parameter( Mandatory )] [string] $Rid )

	$json = Invoke-Native -FilePath 'dotnet' -Capture -Arguments @(
		'msbuild', $Context.ProjectPath, '-nologo', '-m:1', '-t:ProcessFrameworkReferences',
		"-p:RuntimeIdentifier=$Rid", '-getItem:RuntimePack' )

	$pack = ( $json | ConvertFrom-Json ).Items.RuntimePack | Where-Object FrameworkName -eq 'Microsoft.NETCore.App'
	$directory = Join-Path ( Get-NuGetPackagesRoot ) ( Join-Path $pack.NuGetPackageId.ToLowerInvariant() $pack.NuGetPackageVersion )

	if( -not ( Test-Path $directory ) )
	{
		throw "runtime pack not found at $directory"
	}

	return $directory
}

# Licence texts every artifact carries: the product's GPL, the AGPL of the libraries it links,
# the mapping between them, third-party notices and, when self-contained, the runtime's own.
function Copy-DistributionDocs
{
	param( [Parameter( Mandatory )] $Context, [Parameter( Mandatory )] [string] $Destination, [string] $Rid )

	$root = $Context.RepoRoot
	$productDir = Split-Path -Parent $Context.ProjectPath

	Copy-Item ( Join-Path $productDir 'LICENSE' ) ( Join-Path $Destination 'LICENSE.GPL-3.0.txt' )
	Copy-Item ( Join-Path $root 'LICENSE' ) ( Join-Path $Destination 'LICENSE.AGPL-3.0.txt' )
	Copy-Item ( Join-Path $root 'LICENSING.md' ) ( Join-Path $Destination 'LICENSING.md' )
	Copy-Item ( Join-Path $root 'THIRD-PARTY-NOTICES.txt' ) ( Join-Path $Destination 'THIRD-PARTY-NOTICES.txt' )

	if( $Rid )
	{
		$runtimePack = Get-RuntimePackDirectory -Context $Context -Rid $Rid
		Copy-Item ( Join-Path $runtimePack 'LICENSE.TXT' ) ( Join-Path $Destination 'DOTNET-LICENSE.txt' )
		Copy-Item ( Join-Path $runtimePack 'THIRD-PARTY-NOTICES.TXT' ) ( Join-Path $Destination 'DOTNET-THIRD-PARTY-NOTICES.txt' )
	}
}

# Self-contained single-file publish (Installers/Publish.props) plus the licence texts.
function Publish-Product
{
	param( [Parameter( Mandatory )] $Context, [Parameter( Mandatory )] [string] $Rid )

	$output = Get-PublishDir -Context $Context -Rid $Rid

	if( Test-Path $output )
	{
		Remove-Item -Recurse -Force -LiteralPath $output
	}

	Invoke-Native -FilePath 'dotnet' -Arguments @(
		'publish', $Context.ProjectPath, '-m:1', '-nologo', '-c', 'Release', '-r', $Rid,
		"-p:Version=$( $Context.Version )", '-o', $output )

	Copy-DistributionDocs -Context $Context -Destination $output -Rid $Rid

	return $output
}

# <name>-<version>-<rid>.zip on Windows RIDs, .tar.gz elsewhere (tar keeps the executable bit).
# The archive holds a top-level <name>-<version> folder.
function New-Archive
{
	param( [Parameter( Mandatory )] $Context, [Parameter( Mandatory )] [string] $Rid )

	$publish = Get-PublishDir -Context $Context -Rid $Rid
	$folder = "$( $Context.Name )-$( $Context.Version )"
	$stage = Join-Path $Context.WorkDir "archive-$Rid"
	$null = New-Item -ItemType Directory -Force -Path $Context.DistDir

	if( Test-Path $stage )
	{
		Remove-Item -Recurse -Force -LiteralPath $stage
	}

	$null = New-Item -ItemType Directory -Path ( Join-Path $stage $folder )
	Copy-Item -Path ( Join-Path $publish '*' ) -Destination ( Join-Path $stage $folder ) -Recurse

	if( $Rid -like 'win-*' )
	{
		$archive = Join-Path $Context.DistDir "$folder-$Rid.zip"
		Compress-Archive -Path ( Join-Path $stage $folder ) -DestinationPath $archive -Force
	}
	else
	{
		$archive = Join-Path $Context.DistDir "$folder-$Rid.tar.gz"
		Invoke-Native -FilePath 'tar' -Arguments @( '-czf', $archive, '-C', $stage, $folder )
	}

	return $archive
}

function Get-ArchivePath
{
	param( [Parameter( Mandatory )] $Context, [Parameter( Mandatory )] [string] $Rid )

	$extension = $Rid -like 'win-*' ? 'zip' : 'tar.gz'

	return Join-Path $Context.DistDir "$( $Context.Name )-$( $Context.Version )-$Rid.$extension"
}

# A product invoker runs the installed product with arguments and returns its exit code and
# output. Test-ProductCommands is written against invokers only, so the same checks run a binary
# directly, from PATH, or inside a container (Linux.ps1 builds one over 'docker exec').
#
# The local invoker runs in a fresh working directory, which it creates.
function New-LocalInvoker
{
	param( [Parameter( Mandatory )] [string] $Command, [Parameter( Mandatory )] [string] $WorkingDirectory )

	$null = New-Item -ItemType Directory -Force -Path $WorkingDirectory

	return {
		param( [string[]] $Arguments )

		Push-Location $WorkingDirectory
		try
		{
			$output = & $Command @Arguments 2>&1 | Out-String
			$code = $LASTEXITCODE
		}
		finally
		{
			Pop-Location
		}

		return [pscustomobject]@{ ExitCode = $code; Output = $output }
	}.GetNewClosure()
}

function Assert-Invocation
{
	param(
		[Parameter( Mandatory )] [scriptblock] $Invoke,
		[Parameter( Mandatory )] [string[]] $Arguments,
		[int] $ExitCode = 0,
		[string] $Expect
	)

	$result = & $Invoke $Arguments
	Write-Host "  $( $Arguments -join ' ' ) -> $( $result.ExitCode )"

	if( $result.ExitCode -ne $ExitCode )
	{
		throw "'$( $Arguments -join ' ' )' exited with $( $result.ExitCode ), expected $ExitCode`n$( $result.Output )"
	}

	if( $Expect -and $result.Output -notmatch $Expect )
	{
		throw "'$( $Arguments -join ' ' )' printed no match for '$Expect'`n$( $result.Output )"
	}

	return $result
}

# The checks every installed form of a product must pass. -BundlePath and -ProjectPath are the
# smoke fixtures (Installers/smoke/bundle and Installers/smoke/project) as the product sees them,
# which differs inside a container, where both are mounted read-only.
function Test-ProductCommands
{
	param(
		[Parameter( Mandatory )] $Context,
		[Parameter( Mandatory )] [scriptblock] $Invoke,
		[string] $BundlePath = ( Join-Path $script:RepoRoot 'Installers/smoke/bundle' ),
		[string] $ProjectPath = ( Join-Path $script:RepoRoot 'Installers/smoke/project' )
	)

	$null = Assert-Invocation -Invoke $Invoke -Arguments @( '--version' ) -Expect "^$( [regex]::Escape( $Context.Version ) )\s*$"

	switch( $Context.Name )
	{
		'sqetch'
		{
			$null = Assert-Invocation -Invoke $Invoke -Arguments @( 'project', 'user', 'Smoke Test', 'smoke@example.com' )
			$null = Assert-Invocation -Invoke $Invoke -Arguments @( 'project', 'init', 'smoke' )
			$null = Assert-Invocation -Invoke $Invoke -Arguments @( 'plan', 'create', '-n', 'smoke-plan' )
			$null = Assert-Invocation -Invoke $Invoke -Arguments @( 'plan', 'add-step', '-p', 'smoke-plan', '-n', 'smoke-step' )
			$null = Assert-Invocation -Invoke $Invoke -Arguments @( 'plan', 'list' ) -Expect 'smoke-plan'
		}

		'sqetch-deploy'
		{
			$null = Assert-Invocation -Invoke $Invoke -Arguments @( 'drivers', 'list' ) -Expect 'sqlite'

			# a real deployment loads the native e_sqlite3 beside the executable; deploying the same
			# bundle again is 'nothing to deploy', which is also success
			$deploy = @( 'deploy', $BundlePath, '--driver', 'sqlite', '--sqlite-file', 'smoke.db' )
			$null = Assert-Invocation -Invoke $Invoke -Arguments $deploy
			$null = Assert-Invocation -Invoke $Invoke -Arguments $deploy

			# bundling reads the committed project, which needs no project user, and what it writes
			# deploys; the output name is fresh because a bundle refuses an existing target, and a
			# working directory can outlive one run
			$bundled = "smoke-bundle-$( [Guid]::NewGuid().ToString( 'N' ).Substring( 0, 8 ) )"
			$null = Assert-Invocation -Invoke $Invoke -Arguments @( 'bundle', '--project', $ProjectPath, '-t', 'directory', '-o', $bundled )
			$null = Assert-Invocation -Invoke $Invoke -Arguments @( 'deploy', $bundled, '--driver', 'sqlite', '--sqlite-file', 'bundled.db' ) -Expect 'bundle built by sqetch-deploy'
		}
	}

	Write-Host "smoke checks passed for $( $Context.Name ) $( $Context.Version )"
}

# Extracts the host RID's archive and runs the checks on the binary inside.
function Test-Archive
{
	param( [Parameter( Mandatory )] $Context, [Parameter( Mandatory )] [string] $Rid )

	$archive = Get-ArchivePath -Context $Context -Rid $Rid
	$target = Join-Path $Context.WorkDir "smoke-archive-$Rid"

	if( Test-Path $target )
	{
		Remove-Item -Recurse -Force -LiteralPath $target
	}

	$null = New-Item -ItemType Directory -Path $target

	if( $archive -like '*.zip' )
	{
		Expand-Archive -Path $archive -DestinationPath $target
	}
	else
	{
		Invoke-Native -FilePath 'tar' -Arguments @( '-xzf', $archive, '-C', $target )
	}

	$binary = Join-Path ( Join-Path $target "$( $Context.Name )-$( $Context.Version )" ) ( Get-ExecutableName -Context $Context -Rid $Rid )
	$invoke = New-LocalInvoker -Command $binary -WorkingDirectory ( Join-Path $target 'work' )

	Test-ProductCommands -Context $Context -Invoke $invoke
}

# The framework-dependent dotnet tool package (PackAsTool in the host's project).
function New-ToolPackage
{
	param( [Parameter( Mandatory )] $Context )

	Invoke-Native -FilePath 'dotnet' -Arguments @(
		'pack', $Context.ProjectPath, '-m:1', '-nologo', '-c', 'Release',
		"-p:Version=$( $Context.Version )", '-o', $Context.DistDir )

	return Join-Path $Context.DistDir "$( $Context.Product.PackageId ).$( $Context.Version ).nupkg"
}

function Test-ToolPackage
{
	param( [Parameter( Mandatory )] $Context )

	$toolPath = Join-Path $Context.WorkDir 'smoke-tool'

	if( Test-Path $toolPath )
	{
		Remove-Item -Recurse -Force -LiteralPath $toolPath
	}

	Invoke-Native -FilePath 'dotnet' -Arguments @(
		'tool', 'install', $Context.Product.PackageId, '--version', $Context.Version,
		'--tool-path', $toolPath, '--add-source', $Context.DistDir, '--ignore-failed-sources' )

	$command = Join-Path $toolPath ( $IsWindows ? "$( $Context.Name ).exe" : $Context.Name )
	$invoke = New-LocalInvoker -Command $command -WorkingDirectory ( Join-Path $toolPath 'work' )

	Test-ProductCommands -Context $Context -Invoke $invoke
}

# Pushes the tool package pack-tool made to nuget.org with the key in NUGET_API_KEY (in the release
# workflow, the short-lived one trusted publishing issued). A version already there is skipped, so a
# re-run after a push that succeeded completes the job instead of failing it; nuget.org cannot take
# a version back, only unlist it. -DryRun prints the push and leaves the key unread.
function Publish-ToolPackage
{
	param( [Parameter( Mandatory )] $Context, [switch] $DryRun )

	$package = Join-Path $Context.DistDir "$( $Context.Product.PackageId ).$( $Context.Version ).nupkg"

	if( -not ( Test-Path -LiteralPath $package ) )
	{
		throw "nothing to push: $package does not exist; run -Stage pack-tool first"
	}

	$arguments = @( 'nuget', 'push', $package, '--source', $script:NuGetSource, '--skip-duplicate', '--api-key' )

	if( $DryRun )
	{
		Write-Host "[dry run] dotnet $( $arguments -join ' ' ) ***"
		return
	}

	if( -not $env:NUGET_API_KEY )
	{
		throw 'stage push-tool needs NUGET_API_KEY in the environment'
	}

	Invoke-Native -FilePath 'dotnet' -Arguments ( $arguments + $env:NUGET_API_KEY ) -Secret $env:NUGET_API_KEY
}

function Write-Checksums
{
	param( [Parameter( Mandatory )] $Context )

	$sums = Get-ChildItem -File -Path $Context.DistDir |
		Where-Object Name -ne 'SHA256SUMS' |
		Sort-Object Name |
		ForEach-Object { "$( ( Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName ).Hash.ToLowerInvariant() )  $( $_.Name )" }

	$path = Join-Path $Context.DistDir 'SHA256SUMS'
	[IO.File]::WriteAllText( $path, ( $sums -join "`n" ) + "`n" )

	return $path
}

# Stage outputs for the workflow (GITHUB_OUTPUT), echoed for a local run.
function Write-StageOutputs
{
	param( [Parameter( Mandatory )] [System.Collections.IDictionary] $Values )

	foreach( $entry in $Values.GetEnumerator() )
	{
		$value = $entry.Value -is [bool] ? $entry.Value.ToString().ToLowerInvariant() : "$( $entry.Value )"
		Write-Host "$( $entry.Key )=$value"

		if( $env:GITHUB_OUTPUT )
		{
			Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "$( $entry.Key )=$value"
		}
	}
}
