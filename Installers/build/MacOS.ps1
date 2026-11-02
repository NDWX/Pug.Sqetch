# MacOS half of the release build, dot-sourced by build.ps1 after Common.ps1. Implements the contract
# described at the top of Common.ps1.
#
# macOS ships the .tar.gz archive Common makes (Homebrew installs from it) and nothing else. The
# binary is only ad-hoc signed: the SDK does that itself for any osx RID (_EnableMacOSCodeSign in
# Microsoft.NET.Sdk.targets, honoured by both CreateAppHost and the single-file GenerateBundle task),
# and arm64 macOS refuses to run an unsigned binary. Developer ID signing and notarization are
# deferred until the product is distributed beyond Homebrew.

function Invoke-PlatformSigning
{
	param( [Parameter(Mandatory)] $Context, [Parameter(Mandatory)] [string[]] $Files )

	throw 'signing is Authenticode, which is Windows-only; Developer ID signing and notarization for macOS are not implemented yet'
}

function New-PlatformPackages
{
	param( [Parameter(Mandatory)] $Context, [Parameter(Mandatory)] [string] $Rid )

	# nothing beyond the archive
}

# The host RID's binary, as extracted from the archive by Test-Archive, must carry a valid signature.
function Test-Installers
{
	param( [Parameter(Mandatory)] $Context, [Parameter(Mandatory)] [string] $Rid )

	if( $Rid -ne ( Get-HostRid ) )
	{
		Write-Host "skipping the code signature check for $Rid on a $(Get-HostRid) host"
		return
	}

	$binary = Join-Path ( Join-Path ( Join-Path $Context.WorkDir "smoke-archive-$Rid" ) "$($Context.Name)-$($Context.Version)" ) $Context.Name

	if( -not ( Test-Path -LiteralPath $binary ) )
	{
		throw "$binary not found; the archive must be extracted before its signature is checked"
	}

	Invoke-Native -FilePath 'codesign' -Arguments @( '--verify', '--verbose', $binary )

	# codesign writes what it reports to stderr
	$details = & codesign --display --verbose=2 $binary 2>&1 | Out-String
	Write-Host $details.TrimEnd()

	if( $LASTEXITCODE -ne 0 )
	{
		throw "codesign --display exited with code $LASTEXITCODE"
	}

	Write-Host "code signature verified for $($Context.Name) $($Context.Version) $Rid"
}
