# Linux half of the release build, dot-sourced by build.ps1 after Common.ps1. Implements the contract
# described at the top of Common.ps1: a .deb and an .rpm per Linux RID, built with nfpm and smoke
# tested by installing them into a Debian and a Fedora container.

# nfpm, pinned, as its container image; run through Invoke-Docker so a build needs nothing installed
$script:NfpmImage = 'goreleaser/nfpm:v2.47.0'

# the images the packages are installed into; 'latest' on purpose, since a package manager resolving
# the declared dependencies on a current distribution is what the smoke test is for
$script:DebianImage = 'debian:stable'
$script:FedoraImage = 'fedora:latest'

# what Copy-DistributionDocs puts in a publish folder: these go to /usr/share/doc/<name> only
$script:DistributionDocFiles = @(
	'LICENSE.GPL-3.0.txt', 'LICENSE.AGPL-3.0.txt', 'LICENSING.md', 'THIRD-PARTY-NOTICES.txt',
	'DOTNET-LICENSE.txt', 'DOTNET-THIRD-PARTY-NOTICES.txt' )

function Invoke-PlatformSigning
{
	param( [Parameter( Mandatory )] $Context, [Parameter( Mandatory )] [string[]] $Files )

	throw 'signing is Windows-only: Linux packages are unsigned (a signed apt/yum repository belongs to distribution, which is not built yet)'
}

# 'linux-x64' -> the architecture names of nfpm/deb (amd64) and of rpm (x86_64); $null for a RID that is not Linux
function Get-LinuxArchitecture
{
	param( [Parameter( Mandatory )] [string] $Rid )

	switch( $Rid )
	{
		'linux-x64'   { return [pscustomobject]@{ Nfpm = 'amd64'; Rpm = 'x86_64' } }
		'linux-arm64' { return [pscustomobject]@{ Nfpm = 'arm64'; Rpm = 'aarch64' } }
		default       { return $null }
	}
}

# The packages New-PlatformPackages names '<name>_<version>_<arch>.deb' and '<name>-<version>.<arch>.rpm'.
# The patterns cannot match the other product: its name continues '-deploy', and rpm's version starts
# with a digit.
function Get-LinuxPackages
{
	param( [Parameter( Mandatory )] $Context, [Parameter( Mandatory )] $Architecture, [Parameter( Mandatory )] [ValidateSet( 'deb', 'rpm' )] [string] $Kind )

	$pattern = $Kind -eq 'deb' ? "$( $Context.Name )_*_$( $Architecture.Nfpm ).deb" : "$( $Context.Name )-[0-9]*.$( $Architecture.Rpm ).rpm"

	# -like, not -Filter: the file system filter has no [0-9]
	return @( Get-ChildItem -File -Path $Context.DistDir -ErrorAction SilentlyContinue | Where-Object { $_.Name -like $pattern } )
}

# Lays the publish output out as nfpm's template wants it: stage/lib is everything but the licence
# texts (installed under /usr/lib/<name>), stage/doc the licence texts (/usr/share/doc/<name>).
function New-PackageStage
{
	param( [Parameter( Mandatory )] $Context, [Parameter( Mandatory )] [string] $Rid, [Parameter( Mandatory )] $Architecture )

	$publish = Get-PublishDir -Context $Context -Rid $Rid
	$stage = Join-Path $Context.WorkDir "nfpm-$( $Context.Name )-$Rid"

	if( Test-Path $stage )
	{
		Remove-Item -Recurse -Force -LiteralPath $stage
	}

	$lib = Join-Path $stage 'lib'
	$doc = Join-Path $stage 'doc'
	$null = New-Item -ItemType Directory -Path $lib, $doc

	foreach( $file in Get-ChildItem -File -LiteralPath $publish )
	{
		$isDoc = $script:DistributionDocFiles -contains $file.Name
		$copy = Copy-Item -LiteralPath $file.FullName -Destination ( $isDoc ? $doc : $lib ) -PassThru

		# the mode a package installs: whatever the publish output happened to carry (a native library
		# from the NuGet cache arrives rwxr--r--) is not a decision worth shipping
		[IO.File]::SetUnixFileMode( $copy.FullName, [IO.UnixFileMode][Convert]::ToInt32( $isDoc ? '644' : '755', 8 ) )
	}

	$description = $Context.Product.Description.Replace( '\', '\\' ).Replace( '"', '\"' )
	$config = ( Get-Content -Raw -LiteralPath ( Join-Path $Context.RepoRoot 'Installers/nfpm/nfpm.yaml' ) ).
		Replace( '${SQETCH_NAME}', $Context.Name ).
		Replace( '${SQETCH_ARCH}', $Architecture.Nfpm ).
		Replace( '${SQETCH_VERSION}', $Context.Version ).
		Replace( '${SQETCH_DESCRIPTION}', $description )

	[IO.File]::WriteAllText( ( Join-Path $stage 'nfpm.yaml' ), $config )

	return $stage
}

function New-PlatformPackages
{
	param( [Parameter( Mandatory )] $Context, [Parameter( Mandatory )] [string] $Rid )

	$architecture = Get-LinuxArchitecture -Rid $Rid

	if( -not $architecture )
	{
		return
	}

	$stage = New-PackageStage -Context $Context -Rid $Rid -Architecture $architecture
	$null = New-Item -ItemType Directory -Force -Path $Context.DistDir

	# nfpm names a package after its version, so a rebuild at another version would leave this one
	# behind, and Test-Installers (and the release upload) must find exactly one of each
	foreach( $kind in 'deb', 'rpm' )
	{
		Get-LinuxPackages -Context $Context -Architecture $architecture -Kind $kind | Remove-Item -Force
	}

	# as the invoking user, so what lands in dist/ is deletable without root
	$user = "$( Invoke-Native -FilePath 'id' -Arguments @( '-u' ) -Capture ):$( Invoke-Native -FilePath 'id' -Arguments @( '-g' ) -Capture )"

	# the file names carry the version as the release spells it: nfpm's defaults would write a
	# pre-release as '0.1.0~pre.…', and GitHub rewrites '~' in asset names, which would then no longer
	# match SHA256SUMS. Only the file name changes; the package's own version keeps the '~' that sorts
	# it before the final release.
	$targets = @{
		deb = "$( $Context.Name )_$( $Context.Version )_$( $architecture.Nfpm ).deb"
		rpm = "$( $Context.Name )-$( $Context.Version ).$( $architecture.Rpm ).rpm"
	}

	foreach( $kind in 'deb', 'rpm' )
	{
		Invoke-Docker -Arguments @(
			'run', '--rm', '--user', $user,
			'-v', "${stage}:/stage:ro", '-v', "$( $Context.DistDir ):/dist",
			$script:NfpmImage, 'package', '--config', '/stage/nfpm.yaml', '--packager', $kind, '--target', "/dist/$( $targets[$kind] )" )
	}

	foreach( $kind in 'deb', 'rpm' )
	{
		if( @( Get-LinuxPackages -Context $Context -Architecture $architecture -Kind $kind ).Count -ne 1 )
		{
			throw "expected exactly one .$kind for $( $Context.Name ) $Rid in $( $Context.DistDir )"
		}
	}
}

# A product invoker over 'docker exec': the command by its bare name, so PATH (and the /usr/bin
# symlink the package installs) is what is under test, in a fresh working directory inside the container.
function New-ContainerInvoker
{
	param( [Parameter( Mandatory )] [string] $Container, [Parameter( Mandatory )] [string] $Command )

	$null = Invoke-Docker -Arguments @( 'exec', $Container, 'mkdir', '-p', '/smoke/work' )

	# captured by value: a closure cannot see this file's script scope, but a function's scriptblock keeps its own
	$docker = ${function:Invoke-Docker}

	return {
		param( [string[]] $Arguments )

		return & $docker -PassThru -Arguments ( @( 'exec', '--workdir', '/smoke/work', $Container, $Command ) + $Arguments )
	}.GetNewClosure()
}

# Installs one package into a fresh container of $Image, checks what the packager recorded about its
# version, and runs the product checks against what was installed.
function Test-LinuxPackage
{
	param(
		[Parameter( Mandatory )] $Context,
		[Parameter( Mandatory )] [string] $Image,
		[Parameter( Mandatory )] [IO.FileInfo] $Package,
		[Parameter( Mandatory )] [string] $InstallScript,
		# receives the container name; returns nothing, throws when the packager sorts the version wrongly
		[Parameter( Mandatory )] [scriptblock] $AssertVersionOrder
	)

	$container = "sqetch-smoke-$( $Context.Name )-$( [Guid]::NewGuid().ToString( 'N' ).Substring( 0, 8 ) )"
	$bundle = Join-Path $Context.RepoRoot 'Installers/smoke/bundle'
	$project = Join-Path $Context.RepoRoot 'Installers/smoke/project'

	Write-Host "installing $( $Package.Name ) into $Image"

	try
	{
		Invoke-Docker -Arguments @(
			'run', '-d', '--name', $container,
			'-v', "$( $Package.DirectoryName ):/packages:ro", '-v', "${bundle}:/smoke/bundle:ro", '-v', "${project}:/smoke/project:ro",
			$Image, 'sleep', 'infinity' )

		# the package manager, not dpkg/rpm, so the declared dependencies are resolved and fetched
		Invoke-Docker -Arguments @( 'exec', $container, 'sh', '-ec', $InstallScript.Replace( '{package}', "/packages/$( $Package.Name )" ) )

		$link = Invoke-Docker -Capture -Arguments @( 'exec', $container, 'readlink', '-f', "/usr/bin/$( $Context.Name )" )

		if( $link -ne "/usr/lib/$( $Context.Name )/$( $Context.Name )" )
		{
			throw "/usr/bin/$( $Context.Name ) resolves to '$link'"
		}

		if( $Context.Version -ne $Context.BaseVersion )
		{
			& $AssertVersionOrder $container
		}

		$invoke = New-ContainerInvoker -Container $container -Command $Context.Name
		Test-ProductCommands -Context $Context -Invoke $invoke -BundlePath '/smoke/bundle' -ProjectPath '/smoke/project'
	}
	finally
	{
		# $ErrorActionPreference is 'Stop', but a container that never started has nothing to remove
		try { Invoke-Docker -Arguments @( 'rm', '-f', $container ) } catch { Write-Warning "could not remove container ${container}: $_" }
	}
}

function Test-Installers
{
	param( [Parameter( Mandatory )] $Context, [Parameter( Mandatory )] [string] $Rid )

	$architecture = Get-LinuxArchitecture -Rid $Rid

	if( -not $architecture )
	{
		return
	}

	if( $Rid -ne ( Get-HostRid ) )
	{
		Write-Host "skipping the installer smoke test for ${Rid}: this host runs $( Get-HostRid )"

		return
	}

	$name = $Context.Name
	$base = $Context.BaseVersion

	# a prerelease must sort before the release it precedes, or 'apt upgrade' / 'dnf upgrade' would
	# never move a pre-release user onto the release
	$deb = @( Get-LinuxPackages -Context $Context -Architecture $architecture -Kind 'deb' )
	$rpm = @( Get-LinuxPackages -Context $Context -Architecture $architecture -Kind 'rpm' )

	if( $deb.Count -ne 1 -or $rpm.Count -ne 1 )
	{
		throw "expected exactly one .deb and one .rpm for $name $Rid in $( $Context.DistDir ); run -Stage package first"
	}

	Test-LinuxPackage -Context $Context -Image $script:DebianImage -Package $deb[0] `
		-InstallScript 'apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq {package}' `
		-AssertVersionOrder {
			param( $Container )

			$installed = Invoke-Docker -Capture -Arguments @( 'exec', $Container, 'dpkg-query', '-W', '-f=${Version}', $name )
			$null = Invoke-Docker -Arguments @( 'exec', $Container, 'dpkg', '--compare-versions', $installed, 'lt', $base )
		}

	Test-LinuxPackage -Context $Context -Image $script:FedoraImage -Package $rpm[0] `
		-InstallScript 'dnf install -y -q {package}' `
		-AssertVersionOrder {
			param( $Container )

			$installed = Invoke-Docker -Capture -Arguments @( 'exec', $Container, 'rpm', '-q', '--qf', '%{VERSION}-%{RELEASE}', $name )
			$result = Invoke-Docker -Capture -Arguments @( 'exec', $Container, 'rpm', '--eval', "%{lua: print( rpm.vercmp( '$installed', '$base-1' ) )}" )

			if( $result -ne '-1' )
			{
				throw "rpm version $installed does not sort before $base-1 (vercmp $result)"
			}
		}
}
