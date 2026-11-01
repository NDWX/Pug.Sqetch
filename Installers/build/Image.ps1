# The container image of a product that has one (sqetch-deploy): Installers/docker/Dockerfile over
# the product's Linux release archives. Dot-sourced by build.ps1 after Common.ps1. It is not a
# platform file: what it needs is Docker, not a particular OS.
#
# The image is built from the archives the package stage left in dist/, never from a fresh publish,
# so it holds the same files a direct download does, and there is no RUN step, so the arm64 image
# builds on an x64 host without emulation.

# the release archives an image is built from, and the Docker architecture each becomes
$script:ImageArchitectures = [ordered]@{ 'linux-x64' = 'amd64'; 'linux-arm64' = 'arm64' }

function Assert-ImageProduct
{
	param( [Parameter( Mandatory )] $Context )

	if( -not $Context.Product.Image )
	{
		throw "$( $Context.Name ) has no container image"
	}
}

# A fresh build context: the Dockerfile, and rootfs-<arch>/ holding each archive's extracted folder.
function New-ImageBuildContext
{
	param( [Parameter( Mandatory )] $Context, [Parameter( Mandatory )] [string[]] $Rids )

	$root = Join-Path $Context.WorkDir "image-$( $Context.Name )"

	if( Test-Path $root )
	{
		Remove-Item -Recurse -Force -LiteralPath $root
	}

	$null = New-Item -ItemType Directory -Path $root
	Copy-Item -LiteralPath ( Join-Path $script:RepoRoot 'Installers/docker/Dockerfile' ) -Destination $root

	foreach( $rid in $Rids )
	{
		$archive = Get-ArchivePath -Context $Context -Rid $rid

		if( -not ( Test-Path -LiteralPath $archive ) )
		{
			throw "$archive not found; run -Stage package -Rid $rid first"
		}

		$extract = Join-Path $root "extract-$rid"
		$null = New-Item -ItemType Directory -Path $extract
		Invoke-Native -FilePath 'tar' -Arguments @( '-xzf', $archive, '-C', $extract )

		Move-Item -LiteralPath ( Join-Path $extract "$( $Context.Name )-$( $Context.Version )" ) `
			-Destination ( Join-Path $root "rootfs-$( $script:ImageArchitectures[$rid] )" )
		Remove-Item -LiteralPath $extract
	}

	return $root
}

# The labels that change per build; the fixed ones are in the Dockerfile.
function Get-ImageLabelArguments
{
	param( [Parameter( Mandatory )] $Context )

	$revision = Invoke-Native -FilePath 'git' -Arguments @( '-C', $script:RepoRoot, 'rev-parse', 'HEAD' ) -Capture

	return @(
		'--label', "org.opencontainers.image.version=$( $Context.Version )",
		'--label', "org.opencontainers.image.revision=$revision" )
}

# Builds the host architecture's image into the local image store and runs the product checks in it:
# as the image's own user, through its PATH, and through its entrypoint.
function Test-ContainerImage
{
	param( [Parameter( Mandatory )] $Context )

	Assert-ImageProduct -Context $Context

	$rid = Get-HostRid

	if( -not $script:ImageArchitectures.Contains( $rid ) )
	{
		throw "the image smoke test runs on a Linux x64 or arm64 host, not $rid"
	}

	$root = New-ImageBuildContext -Context $Context -Rids @( $rid )
	$image = "$( $Context.Name )-smoke:$( $Context.Version )"
	$container = "sqetch-smoke-image-$( [Guid]::NewGuid().ToString( 'N' ).Substring( 0, 8 ) )"

	Invoke-Docker -Arguments ( @(
		'buildx', 'build', '--platform', "linux/$( $script:ImageArchitectures[$rid] )", '--load', '--tag', $image ) +
		( Get-ImageLabelArguments -Context $Context ) + @( $root ) )

	try
	{
		# the entrypoint is the product, so arguments alone run it
		$entry = Invoke-Docker -PassThru -Arguments @( 'run', '--rm', $image, '--version' )

		if( $entry.ExitCode -ne 0 -or $entry.Output.Trim() -ne $Context.Version )
		{
			throw "'docker run $image --version' exited with $( $entry.ExitCode ) and printed '$( $entry.Output.Trim() )', expected $( $Context.Version )"
		}

		$user = Invoke-Docker -Capture -Arguments @( 'run', '--rm', '--entrypoint', 'id', $image, '-u' )

		if( $user -ne '1654' )
		{
			throw "the image runs as user '$user', expected the base image's non-root 'app' (1654)"
		}

		# the fixtures read-only, as in a pipeline that mounts its checkout; the working directory a
		# tmpfs the non-root user can write, since a bind mount's ownership is the host's business
		$bundle = Join-Path $Context.RepoRoot 'Installers/smoke/bundle'
		$project = Join-Path $Context.RepoRoot 'Installers/smoke/project'

		Invoke-Docker -Arguments @(
			'run', '-d', '--name', $container, '--entrypoint', 'sleep',
			'-v', "${bundle}:/smoke/bundle:ro", '-v', "${project}:/smoke/project:ro",
			'--tmpfs', '/smoke/work:rw,mode=1777',
			$image, 'infinity' )

		$invoke = New-ContainerInvoker -Container $container -Command $Context.Name
		Test-ProductCommands -Context $Context -Invoke $invoke -BundlePath '/smoke/bundle' -ProjectPath '/smoke/project'
	}
	finally
	{
		# $ErrorActionPreference is 'Stop', but a container that never started has nothing to remove
		try { Invoke-Docker -Arguments @( 'rm', '-f', $container ) } catch { Write-Warning "could not remove container ${container}: $_" }
		try { Invoke-Docker -Arguments @( 'image', 'rm', $image ) } catch { Write-Warning "could not remove image ${image}: $_" }
	}
}

# The tags a build is pushed under in each repository: the version, and 'latest' for a release.
function Get-ImageTags
{
	param( [Parameter( Mandatory )] $Context, [Parameter( Mandatory )] [string[]] $Repositories )

	$release = $Context.Version -eq $Context.BaseVersion

	foreach( $repository in $Repositories )
	{
		"${repository}:$( $Context.Version )"

		if( $release )
		{
			"${repository}:latest"
		}
	}
}

# Builds every architecture's image and pushes it, as one multi-platform image, to each repository.
# Needs a buildx builder that can build several platforms (docker/setup-buildx-action in the
# workflow) and a login to each registry. -DryRun builds nothing and prints the command.
function Publish-ContainerImage
{
	param( [Parameter( Mandatory )] $Context, [string[]] $Repositories, [switch] $DryRun )

	Assert-ImageProduct -Context $Context

	if( -not $Repositories )
	{
		throw 'stage push-image needs -Image <repository>[,<repository>]'
	}

	if( $Context.Version -like '*-dev.*' )
	{
		throw "a development build ($( $Context.Version )) is not published"
	}

	foreach( $repository in $Repositories )
	{
		# a repository reference without a tag or digest, in the lowercase Docker requires
		if( $repository -cnotmatch '^[a-z0-9]+([._-][a-z0-9]+)*(\.[a-z0-9-]+)*(:[0-9]+)?(/[a-z0-9]+([._-]+[a-z0-9]+)*)+$' )
		{
			throw "'$repository' is not an image repository such as ghcr.io/owner/name"
		}
	}

	$root = New-ImageBuildContext -Context $Context -Rids @( $script:ImageArchitectures.Keys )
	$platforms = ( $script:ImageArchitectures.Values | ForEach-Object { "linux/$_" } ) -join ','
	$tags = @( Get-ImageTags -Context $Context -Repositories $Repositories | ForEach-Object { '--tag', $_ } )

	$arguments = @( 'buildx', 'build', '--platform', $platforms, '--push' ) + $tags +
		( Get-ImageLabelArguments -Context $Context ) + @( $root )

	if( $DryRun )
	{
		Write-Host "[dry run] docker $( $arguments -join ' ' )"
		return
	}

	Invoke-Docker -Arguments $arguments
}
