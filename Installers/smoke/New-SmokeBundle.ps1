#!/usr/bin/env pwsh
# Regenerates the two smoke fixtures every installed 'sqetch-deploy' is checked against:
#
#   Installers/smoke/project  a Sqetch project, which the smoke test bundles with
#                             'sqetch-deploy bundle --project <project>'
#   Installers/smoke/bundle   a directory bundle of that project, which the smoke test deploys with
#                             'sqetch-deploy deploy <bundle> --driver sqlite --sqlite-file smoke.db'
#
# Both are committed so a smoke test needs no 'sqetch' beside the 'sqetch-deploy' it checks, but they
# are generated files: rebuild them with this script whenever the project format, the bundle format
# or the journaling contract changes, rather than editing them.
#
#   pwsh Installers/smoke/New-SmokeBundle.ps1 [-Sqetch <sqetch executable>] [-SqetchDeploy <sqetch-deploy executable>]
#
# Without either path the script publishes that CLI for this machine and uses it. The project is
# built from scratch in a temporary directory with the real 'sqetch', so the fixture is exactly what
# the authoring commands produce; it is committed without its '.sqetch-user', since bundling needs
# none, and the bundle is then built from the committed copy with the real 'sqetch-deploy'.

[CmdletBinding()]
param(
	[string] $Sqetch,
	[string] $SqetchDeploy
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = ( Resolve-Path ( Join-Path $PSScriptRoot '../..' ) ).Path
$bundleDir = Join-Path $PSScriptRoot 'bundle'
$projectDir = Join-Path $PSScriptRoot 'project'
$scratch = Join-Path ( [IO.Path]::GetTempPath() ) "sqetch-smoke-$( [Guid]::NewGuid().ToString( 'N' ) )"
$null = New-Item -ItemType Directory -Path $scratch

function Invoke-Tool
{
	param( [Parameter( Mandatory )] [string] $Name, [Parameter( Mandatory )] [string] $Path, [Parameter( Mandatory )] [string[]] $Arguments )

	Write-Host "> $Name $( $Arguments -join ' ' )"
	& $Path @Arguments | Out-Host

	if( $LASTEXITCODE -ne 0 )
	{
		throw "$Name exited with code $LASTEXITCODE"
	}
}

function Invoke-Sqetch
{
	param( [Parameter( Mandatory )] [string[]] $Arguments )

	Invoke-Tool -Name 'sqetch' -Path $script:Sqetch -Arguments $Arguments
}

# Publishes a CLI framework-dependent for this machine into the scratch directory; returns the executable.
function Publish-Tool
{
	param( [Parameter( Mandatory )] [string] $Project, [Parameter( Mandatory )] [string] $Name )

	$publishDir = Join-Path $scratch $Name
	& dotnet publish ( Join-Path $repoRoot $Project ) -m:1 -nologo -c Release -o $publishDir | Out-Host

	if( $LASTEXITCODE -ne 0 )
	{
		throw "dotnet publish exited with code $LASTEXITCODE"
	}

	return Join-Path $publishDir ( $IsWindows ? "$Name.exe" : $Name )
}

# A rebuild replaces a fixture rather than merging into it. Only ever one of the two fixture
# folders above, both built from $PSScriptRoot, never a path from outside.
function Clear-Fixture
{
	param( [Parameter( Mandatory )] [ValidateScript( { $_ -eq $bundleDir -or $_ -eq $projectDir } )] [string] $Path )

	if( Test-Path -LiteralPath $Path )
	{
		Remove-Item -Recurse -Force -LiteralPath $Path
	}
}

# Statement files are written LF with a trailing newline, whatever the platform running this is.
function Write-SqlFile
{
	param( [Parameter( Mandatory )] [string] $Path, [Parameter( Mandatory )] [string] $Sql )

	[IO.File]::WriteAllText( $Path, $Sql.Replace( "`r`n", "`n" ).TrimEnd() + "`n" )
}

# The journaling SQL a maintainer could write for SQLite: one append-only row per event. It mirrors
# Pug.Sqetch.Tests.DatabaseDrivers/SqliteJournal.cs, which runs the same shape against the engine.
$table = 'sqetch_journal'

# slot -> the parameters it carries beyond the project every slot has (JournalingSlots.ParameterNames)
$eventSlots = [ordered]@{
	DeployingRelease   = @( 'release', 'description', 'utcTimestamp' )
	DeployingPlan      = @( 'release', 'plan', 'description', 'utcTimestamp' )
	DeployingStep      = @( 'release', 'plan', 'step', 'description', 'utcTimestamp' )
	StepDeployed       = @( 'release', 'plan', 'step', 'description', 'utcTimestamp' )
	PlanDeployed       = @( 'release', 'plan', 'description', 'utcTimestamp' )
	ReleaseDeployed    = @( 'release', 'description', 'utcTimestamp' )
	RollingBackRelease = @( 'release', 'utcTimestamp' )
	RollingBackPlan    = @( 'release', 'plan', 'utcTimestamp' )
	RollingBackStep    = @( 'release', 'plan', 'step', 'utcTimestamp' )
	RolledBackStep     = @( 'release', 'plan', 'step', 'utcTimestamp' )
	RolledBackPlan     = @( 'release', 'plan', 'utcTimestamp' )
	RolledBackRelease  = @( 'release', 'utcTimestamp' )
}

$journaling = [ordered]@{}

# two statements, so a multi-statement slot is part of the fixture; both are idempotent because
# PrepareJournal runs on every deployment
$journaling['PrepareJournal'] = @"
create table if not exists $table (
	id integer primary key autoincrement,
	project text not null,
	slot text not null,
	release text not null,
	plan text,
	step text,
	description text,
	at_utc text not null
)
;;
create index if not exists ${table}_release on $table ( project, release )
"@

$journaling['GetLatestRelease'] = @"
select j.release,
		( select count( * ) from $table c
			where c.project = @project and c.release = j.release and c.slot = 'ReleaseDeployed' )
from $table j
where j.project = @project and j.slot = 'DeployingRelease'
order by j.id desc
limit 1
"@

$journaling['GetDeployedPlans'] = @"
select plan from $table
where project = @project and release = @release and slot = 'PlanDeployed'
"@

foreach( $slot in $eventSlots.Keys )
{
	$columns = @( 'project', 'slot' ) + @( $eventSlots[$slot] | ForEach-Object { $_ -eq 'utcTimestamp' ? 'at_utc' : $_ } )
	$values = @( '@project', "'$slot'" ) + @( $eventSlots[$slot] | ForEach-Object { "@$_" } )
	$journaling[$slot] = "insert into $table ( $( $columns -join ', ' ) ) values ( $( $values -join ', ' ) )"
}

try
{
	if( -not $Sqetch )
	{
		$Sqetch = Publish-Tool -Project 'Pug.Sqetch.Cli/Pug.Sqetch.Cli.csproj' -Name 'sqetch'
	}

	if( -not $SqetchDeploy )
	{
		$SqetchDeploy = Publish-Tool -Project 'Pug.Sqetch.Deployment.Cli/Pug.Sqetch.Deployment.Cli.csproj' -Name 'sqetch-deploy'
	}

	$script:Sqetch = $Sqetch
	$project = Join-Path $scratch 'project'
	$null = New-Item -ItemType Directory -Path $project

	Push-Location $project
	try
	{
		Invoke-Sqetch @( 'project', 'user', 'Smoke Test', 'smoke@example.com' )
		Invoke-Sqetch @( 'project', 'init', 'smoke', '-d', 'Fixture for the installer smoke tests' )

		foreach( $slot in $journaling.Keys )
		{
			# --file, not the argument: a statement may begin with '--', which parses as an option
			$file = Join-Path $scratch "$slot.sql"
			Write-SqlFile -Path $file -Sql $journaling[$slot]
			Invoke-Sqetch @( 'journaling', 'set', $slot, '--file', $file )
		}

		Invoke-Sqetch @( 'plan', 'create', '-n', 'create-greetings', '-d', 'Creates the greetings table' )
		Invoke-Sqetch @( 'plan', 'add-step', '-p', 'create-greetings', '-n', 'greetings-table', '-d', 'The greetings table' )

		# an unreleased plan's step scripts live under plans/; assigning it to a release moves them
		$step = Join-Path $project 'plans/create-greetings/steps/greetings-table'
		Write-SqlFile -Path ( Join-Path $step 'deploy.sql' ) -Sql 'create table greetings ( id integer primary key, message text not null )'
		Write-SqlFile -Path ( Join-Path $step 'verify.sql' ) -Sql 'select id, message from greetings'
		Write-SqlFile -Path ( Join-Path $step 'rollback.sql' ) -Sql 'drop table greetings'

		Invoke-Sqetch @( 'release', 'create', '-n', 'smoke-1', '-d', 'The only release of the smoke fixture', '--plans', 'create-greetings' )
		Invoke-Sqetch @( 'release', 'finalize', '-n', 'smoke-1' )
	}
	finally
	{
		Pop-Location
	}

	# the acting user is authoring's business; bundling must work without one, which the committed
	# fixture proves by not carrying it
	Remove-Item -Force -LiteralPath ( Join-Path $project '.sqetch-user' )

	Clear-Fixture -Path $projectDir
	Copy-Item -Recurse -LiteralPath $project -Destination $projectDir

	Clear-Fixture -Path $bundleDir
	Invoke-Tool -Name 'sqetch-deploy' -Path $SqetchDeploy -Arguments @(
		'bundle', '--project', $projectDir, '--finalized', '-t', 'directory', '-o', $bundleDir )
}
finally
{
	Remove-Item -Recurse -Force -LiteralPath $scratch
}
