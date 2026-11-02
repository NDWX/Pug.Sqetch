# Installs the embedded MSI silently for all users (ALLUSERS=1: Program Files, system PATH). A per-user
# install is the MSI's own business and is not offered through Chocolatey, whose installs are
# machine-wide. Uninstalling needs no script: Chocolatey's auto uninstaller removes what the MSI registered.
$ErrorActionPreference = 'Stop'

$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$msi = @( Get-ChildItem -Path $toolsDir -Filter '*.msi' -File )

if( $msi.Count -ne 1 )
{
	throw "expected exactly one MSI in $toolsDir, found $($msi.Count)"
}

$log = Join-Path $env:TEMP "$($env:ChocolateyPackageName)-$($env:ChocolateyPackageVersion).msi.log"

$packageArgs = @{
	packageName = $env:ChocolateyPackageName
	fileType = 'msi'
	file = $msi[0].FullName
	silentArgs = "ALLUSERS=1 /qn /norestart /l*v `"$log`""
	validExitCodes = @( 0, 3010, 1641 )
}

Install-ChocolateyInstallPackage @packageArgs
