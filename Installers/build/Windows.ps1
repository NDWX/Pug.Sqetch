# Windows half of the release build, dot-sourced by build.ps1 after Common.ps1. Implements the contract
# described at the top of Common.ps1:
#
#   signing     the .NET 'sign' CLI (a local tool pinned in .config/dotnet-tools.json) against a certificate in
#               Azure Key Vault; authentication is the Azure CLI session the workflow's azure/login step opened
#   packages    one MSI per architecture (WiX 5, Installers/Wix) and, from the win-x64 leg, the Chocolatey
#               package that embeds that MSI (Installers/chocolatey)
#   smoke tests install each MSI per user and per machine, then the Chocolatey package, from a fresh
#               process-independent view of PATH; needs an elevated session

$script:WixProjects = @{
	'sqetch'        = 'Installers/Wix/Sqetch/Sqetch.wixproj'
	'sqetch-deploy' = 'Installers/Wix/SqetchDeploy/SqetchDeploy.wixproj'
}

# RID -> WiX platform (the MSBuild Platform of the wixproj)
$script:WixPlatforms = @{
	'win-x64'   = 'x64'
	'win-arm64' = 'arm64'
}

$script:TimestampUrl = 'http://timestamp.acs.microsoft.com'
$script:ProjectUrl = 'https://github.com/NDWX/Pug.Sqetch'

function Invoke-PlatformSigning
{
	param( [Parameter( Mandatory )] $Context, [Parameter( Mandatory )] [string[]] $Files )

	# AZURE_CLIENT_ID and AZURE_TENANT_ID are azure/login's business, not ours
	$missing = @( 'SIGN_KEY_VAULT_URL', 'SIGN_CERTIFICATE_NAME' | Where-Object { -not [Environment]::GetEnvironmentVariable( $_ ) } )

	if( $missing )
	{
		throw "signing needs these environment variables: $( $missing -join ', ' )"
	}

	# A local tool is found through the manifest above the working directory. The manifest pins the
	# prerelease 0.9.1-beta.* of the 'sign' package: the 1.x versions under that id are an unrelated, older
	# package that is not a .NET tool, so the version must be bumped within 0.9.x deliberately.
	Push-Location $Context.RepoRoot
	try
	{
		Invoke-Native -FilePath 'dotnet' -Arguments @( 'tool', 'restore' )

		# 'dotnet sign' is the manifest's 'sign' command; every file is signed and timestamped in one run
		Invoke-Native -FilePath 'dotnet' -Arguments ( @( 'sign', 'code', 'azure-key-vault' ) + $Files + @(
			'--azure-credential-type', 'azure-cli',
			'--azure-key-vault-url', $env:SIGN_KEY_VAULT_URL,
			'--azure-key-vault-certificate', $env:SIGN_CERTIFICATE_NAME,
			'--timestamp-url', $script:TimestampUrl,
			'--description', $Context.Product.Description,
			'--description-url', $script:ProjectUrl ) )
	}
	finally
	{
		Pop-Location
	}

	foreach( $file in $Files )
	{
		Assert-Signed -Path $file
	}
}

# Signed by anyone; whether the chain is trusted is the platform's call and varies by machine.
function Assert-Signed
{
	param( [Parameter( Mandatory )] [string] $Path )

	if( ( Get-AuthenticodeSignature -LiteralPath $Path ).Status -eq 'NotSigned' )
	{
		throw "$Path carries no Authenticode signature"
	}
}

# Chocolatey 2.0 and later speak SemVer 2.0.0, which accepts the dotted pre-release label of
# 0.1.0-pre.202610051000 as a package version as it stands. Chocolatey CLI 2.7.4 packed that version and
# installed it with an exact --version and no --pre, so the release version is used unchanged and nothing
# maps it to a legacy 0.1.0-pre202610051000 form. Chocolatey 1.x would not take it, hence the check in
# New-ChocolateyPackage; the community repository does not take SemVer 2.0.0 either, which does not matter
# while nothing is pushed there.
function Get-ChocolateyVersion
{
	param( [Parameter( Mandatory )] $Context )

	return $Context.Version
}

function Get-InstallerPath
{
	param( [Parameter( Mandatory )] $Context, [Parameter( Mandatory )] [string] $Rid )

	return Join-Path $Context.DistDir "$( $Context.Name )-$( $Context.Version )-$Rid.msi"
}

function New-PlatformPackages
{
	param( [Parameter( Mandatory )] $Context, [Parameter( Mandatory )] [string] $Rid )

	$msi = New-Installer -Context $Context -Rid $Rid

	if( $Rid -eq 'win-x64' )
	{
		$null = New-ChocolateyPackage -Context $Context -Msi $msi
	}
}

# Builds the product's MSI from its publish directory, copies it into dist and signs that copy.
function New-Installer
{
	param( [Parameter( Mandatory )] $Context, [Parameter( Mandatory )] [string] $Rid )

	$platform = $script:WixPlatforms[$Rid]

	if( -not $platform )
	{
		throw "no MSI is built for $Rid; expected one of: $( $script:WixPlatforms.Keys -join ', ' )"
	}

	$publish = Get-PublishDir -Context $Context -Rid $Rid
	$executable = Join-Path $publish ( Get-ExecutableName -Context $Context -Rid $Rid )

	if( -not ( Test-Path -LiteralPath $executable ) )
	{
		throw "$executable not found; run the publish stage for $Rid first"
	}

	if( $Context.Sign -and ( Get-AuthenticodeSignature -LiteralPath $executable ).Status -eq 'NotSigned' )
	{
		throw "$executable is not signed; run the publish stage with -Sign before packaging a signed build"
	}

	$project = Join-Path $Context.RepoRoot $script:WixProjects[$Context.Name]
	$name = "$( $Context.Name )-$( $Context.Version )-$Rid"

	# The ProductVersion of an MSI is numeric X.Y.Z only; the full version is the file name and, for a
	# pre-release, part of the product name. Platform picks bin/<platform>/Release, so the two
	# architectures never share output.
	Invoke-Native -FilePath 'dotnet' -Arguments @(
		'build', $project, '-m:1', '-nologo', '--no-incremental', '-c', 'Release',
		"-p:Platform=$platform",
		"-p:ProductVersion=$( $Context.BaseVersion )",
		"-p:ProductFullVersion=$( $Context.Version )",
		"-p:ProductSourceDir=$publish",
		"-p:OutputName=$name" )

	$built = Join-Path ( Split-Path -Parent $project ) "bin/$platform/Release/$name.msi"

	if( -not ( Test-Path -LiteralPath $built ) )
	{
		throw "WiX did not produce $built"
	}

	$null = New-Item -ItemType Directory -Force -Path $Context.DistDir
	$msi = Get-InstallerPath -Context $Context -Rid $Rid
	Copy-Item -LiteralPath $built -Destination $msi -Force

	if( $Context.Sign )
	{
		Invoke-PlatformSigning -Context $Context -Files @( $msi )
	}

	return $msi
}

# The package embeds the x64 MSI and runs it silently for all users. Its VERIFICATION.txt is rendered
# here because it names this build's file and hash; its LICENSE.txt gets the full licence texts appended.
function New-ChocolateyPackage
{
	param( [Parameter( Mandatory )] $Context, [Parameter( Mandatory )] [string] $Msi )

	if( -not ( Get-Command 'choco' -ErrorAction SilentlyContinue ) )
	{
		throw 'choco is not installed; it is preinstalled on GitHub Windows runners'
	}

	$chocoVersionText = ( Invoke-Native -FilePath 'choco' -Arguments @( '--version' ) -Capture ) -split '-'
	$chocoVersion = [version] $chocoVersionText[0].Trim()

	if( $chocoVersion.Major -lt 2 )
	{
		throw "Chocolatey $chocoVersion cannot pack SemVer 2.0.0 versions such as $( $Context.Version ); version 2.0 or later is required"
	}

	$source = Join-Path $Context.RepoRoot "Installers/chocolatey/$( $Context.Name )"
	$stage = Join-Path $Context.WorkDir "chocolatey-$( $Context.Name )"
	$tools = Join-Path $stage 'tools'
	$msiName = Split-Path -Leaf $Msi

	if( Test-Path $stage )
	{
		Remove-Item -Recurse -Force -LiteralPath $stage
	}

	$null = New-Item -ItemType Directory -Path $stage
	Copy-Item -Path ( Join-Path $source '*' ) -Destination $stage -Recurse
	Copy-Item -LiteralPath $Msi -Destination $tools

	$verification = Join-Path $tools 'VERIFICATION.txt'
	$rendered = ( Get-Content -Raw -LiteralPath $verification ).
		Replace( '{{PRODUCT}}', $Context.Name ).
		Replace( '{{VERSION}}', $Context.Version ).
		Replace( '{{MSI}}', $msiName ).
		Replace( '{{URL}}', ( Get-ReleaseAssetUrl -Context $Context -FileName $msiName ) ).
		Replace( '{{SHA256}}', ( Get-FileHash -Algorithm SHA256 -LiteralPath $Msi ).Hash.ToLowerInvariant() )
	[IO.File]::WriteAllText( $verification, $rendered, [Text.UTF8Encoding]::new( $false ) )

	$licence = Join-Path $tools 'LICENSE.txt'
	$licenceText = Get-Content -Raw -LiteralPath $licence
	$texts = @(
		@{ Title = 'GNU General Public License, version 3 (GPL-3.0-or-later)'; Path = Join-Path $Context.RepoRoot 'Installers/LICENSE' },
		@{ Title = 'GNU Affero General Public License, version 3 (AGPL-3.0-or-later)'; Path = Join-Path $Context.RepoRoot 'LICENSE' } )

	foreach( $entry in $texts )
	{
		$licenceText += "`n`n$( '=' * 100 )`n$( $entry.Title )`n$( '=' * 100 )`n`n" + ( Get-Content -Raw -LiteralPath $entry.Path )
	}

	[IO.File]::WriteAllText( $licence, $licenceText, [Text.UTF8Encoding]::new( $false ) )

	$null = New-Item -ItemType Directory -Force -Path $Context.DistDir
	Invoke-Native -FilePath 'choco' -Arguments @(
		'pack', ( Join-Path $stage "$( $Context.Name ).nuspec" ),
		'--version', ( Get-ChocolateyVersion -Context $Context ),
		'--outputdirectory', $Context.DistDir, '--no-color' )

	return Join-Path $Context.DistDir "$( $Context.Name ).$( Get-ChocolateyVersion -Context $Context ).nupkg"
}

# Where the workflow publishes a build's asset, from the tag scheme in Common.ps1; a development build
# has no release.
function Get-ReleaseAssetUrl
{
	param( [Parameter( Mandatory )] $Context, [Parameter( Mandatory )] [string] $FileName )

	$prefix = $Context.Product.TagName

	if( $Context.Version -eq $Context.BaseVersion )
	{
		$tag = "$prefix-v$( $Context.BaseVersion )"
	}
	elseif( $Context.Version -like '*-pre.*' )
	{
		$tag = "$prefix-pre-v$( $Context.BaseVersion )"
	}
	else
	{
		return '(a development build is not published)'
	}

	return "$script:ProjectUrl/releases/download/$tag/$FileName"
}

# --- smoke tests ----------------------------------------------------------------------------------------

function Test-Installers
{
	param( [Parameter( Mandatory )] $Context, [Parameter( Mandatory )] [string] $Rid )

	if( $Rid -ne ( Get-HostRid ) )
	{
		Write-Host "skipping installer tests for $( $Rid ): this machine runs $( Get-HostRid )"
		return
	}

	$principal = [Security.Principal.WindowsPrincipal]::new( [Security.Principal.WindowsIdentity]::GetCurrent() )

	if( -not $principal.IsInRole( [Security.Principal.WindowsBuiltInRole]::Administrator ) )
	{
		throw 'installer tests install per machine too and need an elevated session'
	}

	$msi = Get-InstallerPath -Context $Context -Rid $Rid

	if( -not ( Test-Path -LiteralPath $msi ) )
	{
		throw "$msi not found; run the package stage first"
	}

	Test-MsiScope -Context $Context -Msi $msi -Scope 'user'
	Test-MsiScope -Context $Context -Msi $msi -Scope 'machine'

	if( $Rid -eq 'win-x64' )
	{
		Test-ChocolateyPackage -Context $Context
	}
}

# msiexec is a GUI-subsystem program, so it is started and waited for rather than called. Arguments with
# white space are quoted, the value of NAME=value ones included.
function Invoke-Msiexec
{
	param( [Parameter( Mandatory )] [string[]] $Arguments, [Parameter( Mandatory )] [string] $Log )

	$all = $Arguments + @( '/qn', '/norestart', '/l*v', $Log )
	$line = ( $all | ForEach-Object {
		if( $_ -notmatch '\s' ) { $_ }
		elseif( $_ -match '^([A-Za-z_][A-Za-z0-9_]*)=(.*)$' ) { "$( $Matches[1] )=`"$( $Matches[2] )`"" }
		else { "`"$_`"" }
	} ) -join ' '

	Write-Host "> msiexec $line"
	$process = Start-Process -FilePath 'msiexec.exe' -ArgumentList $line -Wait -PassThru

	# 3010: success, restart wanted; nothing here installs a driver or a service, but it is not a failure
	if( $process.ExitCode -notin 0, 3010 )
	{
		if( Test-Path -LiteralPath $Log )
		{
			Write-Host "--- tail of $Log"
			Get-Content -LiteralPath $Log -Tail 80 | Out-Host
		}

		throw "msiexec exited with code $( $process.ExitCode )"
	}
}

# The PATH as the registry holds it now, not the copy this process started with.
function Get-RegistryPath
{
	param( [Parameter( Mandatory )] [ValidateSet( 'User', 'Machine' )] [string] $Scope )

	return [Environment]::GetEnvironmentVariable( 'Path', $Scope )
}

function ConvertTo-PathKey
{
	param( [Parameter( Mandatory )] [string] $Path )

	return [IO.Path]::GetFullPath( [Environment]::ExpandEnvironmentVariables( $Path ) ).TrimEnd( '\' )
}

function Get-PathEntries
{
	param( [string] $Path )

	return @( ( $Path -split ';' ) | Where-Object { $_.Trim() } | ForEach-Object { ConvertTo-PathKey $_.Trim() } )
}

# What running '<name>.exe' would find in a PATH, in order; $null when nothing does.
function Resolve-OnPath
{
	param( [Parameter( Mandatory )] [string] $FileName, [string] $Path )

	foreach( $entry in Get-PathEntries $Path )
	{
		# not Join-Path, which throws for an entry on a drive that does not exist
		$candidate = [IO.Path]::Combine( $entry, $FileName )

		if( Test-Path -LiteralPath $candidate -PathType Leaf )
		{
			return $candidate
		}
	}

	return $null
}

# One round: install the MSI in a scope, check where it landed and that it runs from the PATH the
# registry now holds, uninstall, check that it left nothing behind.
function Test-MsiScope
{
	param(
		[Parameter( Mandatory )] $Context,
		[Parameter( Mandatory )] [string] $Msi,
		[Parameter( Mandatory )] [ValidateSet( 'user', 'machine' )] [string] $Scope
	)

	$name = $Context.Name
	$executableName = "$name.exe"

	if( $Scope -eq 'user' )
	{
		# the documented way to ask a dual-purpose package for the per-user context
		$scopeProperties = @( 'ALLUSERS=2', 'MSIINSTALLPERUSER=1' )
		$expected = Join-Path $env:LOCALAPPDATA "Programs\$name"
		$pathScope = 'User'
		$otherScope = 'Machine'
	}
	else
	{
		$scopeProperties = @( 'ALLUSERS=1' )
		$expected = Join-Path $env:ProgramFiles $name
		$pathScope = 'Machine'
		$otherScope = 'User'
	}

	$expectedKey = ConvertTo-PathKey $expected
	$log = Join-Path $Context.WorkDir "msi-$Scope-$name.log"
	$null = New-Item -ItemType Directory -Force -Path $Context.WorkDir

	Write-Host "installer test: $name, $Scope scope, expecting $expected"

	if( Test-Path -LiteralPath $expected )
	{
		throw "$expected exists already; uninstall $name before running the installer tests"
	}

	# There is no install folder option: these name the folder under every spelling it could have, and none may take.
	$decoy = Join-Path ( [IO.Path]::GetTempPath() ) "$name-ignored-install-folder"
	$overrides = @( "INSTALLFOLDER=$decoy", "APPLICATIONFOLDER=$decoy", "InstallFolder=$decoy" )

	Invoke-Msiexec -Arguments ( @( '/i', $Msi ) + $scopeProperties + $overrides ) -Log $log

	$failed = $true
	try
	{
		if( -not ( Test-Path -LiteralPath ( Join-Path $expected $executableName ) ) )
		{
			throw "$executableName was not installed to $expected"
		}

		if( Test-Path -LiteralPath $decoy )
		{
			throw "the install folder was overridden: $decoy exists"
		}

		foreach( $licenceFile in 'LICENSING.md', 'LICENSE.GPL-3.0.txt', 'LICENSE.AGPL-3.0.txt', 'THIRD-PARTY-NOTICES.txt' )
		{
			if( -not ( Test-Path -LiteralPath ( Join-Path $expected $licenceFile ) ) )
			{
				throw "$licenceFile was not installed to $expected"
			}
		}

		$found = Resolve-OnPath -FileName $executableName -Path ( Get-RegistryPath $pathScope )

		if( -not $found -or ( ConvertTo-PathKey ( Split-Path -Parent $found ) ) -ne $expectedKey )
		{
			throw "$executableName does not resolve to $expected through the $pathScope PATH (resolved: $found)"
		}

		if( Get-PathEntries ( Get-RegistryPath $otherScope ) | Where-Object { $_ -ieq $expectedKey } )
		{
			throw "$expected was added to the $otherScope PATH as well"
		}

		$invoke = New-LocalInvoker -Command $found -WorkingDirectory ( Join-Path $Context.WorkDir "smoke-msi-$Scope-$name" )
		Test-ProductCommands -Context $Context -Invoke $invoke
		$failed = $false
	}
	finally
	{
		if( $failed )
		{
			# a failed check: leave the machine as it was found, and let the failure through
			try { Invoke-Msiexec -Arguments @( '/x', $Msi ) -Log "$log.cleanup.log" } catch { Write-Warning "cleanup failed: $_" }
		}
	}

	Invoke-Msiexec -Arguments @( '/x', $Msi ) -Log "$log.uninstall.log"

	if( Test-Path -LiteralPath $expected )
	{
		throw "uninstalling left $expected behind"
	}

	if( Get-PathEntries ( Get-RegistryPath $pathScope ) | Where-Object { $_ -ieq $expectedKey } )
	{
		throw "uninstalling left $expected on the $pathScope PATH"
	}

	Write-Host "installer test passed: $name, $Scope scope"
}

# choco is started as a process rather than called, so that a hang fails the job with a diagnosis instead
# of holding it until the job's timeout (a release run once sat in 'choco install' for 45 minutes without
# choco printing a line). Its output goes to a file echoed as it grows; a call still running after
# $TimeoutMinutes has the processes it started listed and then killed, and Chocolatey's log and the MSI
# log printed. Chocolatey's log is printed on any failure too: it holds the debug detail
# the console output leaves out.
function Invoke-Chocolatey
{
	param(
		[Parameter( Mandatory )] [string[]] $Arguments,
		[string] $MsiLog,
		[double] $TimeoutMinutes = 5
	)

	$choco = ( Get-Command 'choco' -CommandType Application -ErrorAction Stop | Select-Object -First 1 ).Source
	$line = ( $Arguments | ForEach-Object { $_ -match '\s' ? "`"$_`"" : $_ } ) -join ' '
	$output = Join-Path ( [IO.Path]::GetTempPath() ) "choco-$( [guid]::NewGuid() ).out"
	$errors = "$output.err"

	Write-Host "> choco $line"
	$process = Start-Process -FilePath $choco -ArgumentList $line -NoNewWindow -PassThru `
		-RedirectStandardOutput $output -RedirectStandardError $errors
	# without the handle, a process started this way may report no exit code once it has exited
	$null = $process.Handle

	$deadline = [DateTime]::UtcNow.AddMinutes( $TimeoutMinutes )
	$shown = 0
	$finished = $false

	while( -not $finished -and [DateTime]::UtcNow -lt $deadline )
	{
		$finished = $process.WaitForExit( 2000 )
		$shown = Write-NewOutput -Path $output -Shown $shown -Final:$finished
	}

	if( Test-Path -LiteralPath $errors )
	{
		Get-Content -LiteralPath $errors | Out-Host
	}

	$failure = $null

	if( -not $finished )
	{
		Write-Host "--- choco did not finish within $TimeoutMinutes minutes; the processes it started:"
		Get-ProcessTree -Id $process.Id | Out-Host
		$process.Kill( $true )
		$failure = "choco $( $Arguments[0] ) did not finish within $TimeoutMinutes minutes"
	}
	elseif( $process.ExitCode -ne 0 )
	{
		$failure = "choco exited with code $( $process.ExitCode )"
	}

	Remove-Item -LiteralPath $output, $errors -Force -ErrorAction SilentlyContinue

	if( $failure )
	{
		$chocolateyLog = Join-Path ( $env:ChocolateyInstall ?? "$env:ProgramData\chocolatey" ) 'logs\chocolatey.log'

		foreach( $log in @( $chocolateyLog, $MsiLog ) | Where-Object { $_ -and ( Test-Path -LiteralPath $_ ) } )
		{
			Write-Host "--- tail of $log"
			Get-Content -LiteralPath $log -Tail 80 | Out-Host
		}

		throw $failure
	}
}

# Echoes the lines of a file another process is still writing that were not echoed yet, and returns how
# many have been. Until -Final, a last line without its line break is left for the next call.
function Write-NewOutput
{
	param( [Parameter( Mandatory )] [string] $Path, [int] $Shown, [switch] $Final )

	if( -not ( Test-Path -LiteralPath $Path ) )
	{
		return $Shown
	}

	$stream = [IO.File]::Open( $Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite )

	try
	{
		$text = [IO.StreamReader]::new( $stream ).ReadToEnd()
	}
	finally
	{
		$stream.Dispose()
	}

	$lines = $text -split "`r?`n"
	# the text after the last line break: unfinished until -Final, and nothing at all when the text ends
	# with one
	$complete = $Final -and $lines[-1] ? $lines.Count : $lines.Count - 1

	for( $i = $Shown; $i -lt $complete; $i++ )
	{
		Write-Host $lines[$i]
	}

	return [Math]::Max( $Shown, $complete )
}

# A process and everything it started, with the command lines, to show what a hung one is waiting on.
function Get-ProcessTree
{
	param( [Parameter( Mandatory )] [int] $Id )

	$all = @( Get-CimInstance -ClassName Win32_Process )
	$ids = [Collections.Generic.HashSet[int]]::new()
	$null = $ids.Add( $Id )
	$grew = $true

	while( $grew )
	{
		$grew = $false

		foreach( $candidate in $all )
		{
			if( $ids.Contains( [int] $candidate.ParentProcessId ) -and $ids.Add( [int] $candidate.ProcessId ) )
			{
				$grew = $true
			}
		}
	}

	return $all |
		Where-Object { $ids.Contains( [int] $_.ProcessId ) } |
		Format-Table -AutoSize -Wrap ProcessId, ParentProcessId, Name, CommandLine
}

# choco is given the directory with the package as its only source and the exact version.
function Test-ChocolateyPackage
{
	param( [Parameter( Mandatory )] $Context )

	$id = $Context.Name
	$expected = Join-Path $env:ProgramFiles $id
	$expectedKey = ConvertTo-PathKey $expected
	$version = Get-ChocolateyVersion -Context $Context
	$log = Join-Path $env:TEMP "$id-$version.msi.log"

	Write-Host "chocolatey test: $id $version"

	if( Test-Path -LiteralPath $expected )
	{
		throw "$expected exists already; uninstall $id before running the Chocolatey test"
	}

	$prerelease = $version.Contains( '-' ) ? @( '--prerelease' ) : @()
	Invoke-Chocolatey -Arguments ( @( 'install', $id, '--source', $Context.DistDir, '--version', $version, '-y', '--no-progress', '--no-color' ) + $prerelease ) -MsiLog $log

	$failed = $true
	try
	{
		$found = Resolve-OnPath -FileName "$id.exe" -Path ( Get-RegistryPath 'Machine' )

		if( -not $found -or ( ConvertTo-PathKey ( Split-Path -Parent $found ) ) -ne $expectedKey )
		{
			throw "$id.exe does not resolve to $expected through the Machine PATH (resolved: $found)"
		}

		$invoke = New-LocalInvoker -Command $found -WorkingDirectory ( Join-Path $Context.WorkDir "smoke-choco-$id" )
		Test-ProductCommands -Context $Context -Invoke $invoke
		$failed = $false
	}
	finally
	{
		if( $failed )
		{
			try { Invoke-Chocolatey -Arguments @( 'uninstall', $id, '-y', '--no-color' ) } catch { Write-Warning "cleanup failed: $_" }
		}
	}

	Invoke-Chocolatey -Arguments @( 'uninstall', $id, '-y', '--no-color' )

	if( Test-Path -LiteralPath $expected )
	{
		throw "choco uninstall left $expected behind"
	}

	if( Get-PathEntries ( Get-RegistryPath 'Machine' ) | Where-Object { $_ -ieq $expectedKey } )
	{
		throw "choco uninstall left $expected on the Machine PATH"
	}

	Write-Host "chocolatey test passed: $id"
}
