using System.ComponentModel;

using Pug.Sqetch.Cli;
using Pug.Sqetch.Deployment.Bundling;
using Pug.Sqetch.DatabaseDriver;
using Pug.Sqetch.Planning.Models;

using Spectre.Console;
using Spectre.Console.Cli;

namespace Pug.Sqetch.Deployment.Cli.Commands;

public sealed class StatusSettings : OutputSettings
{
	[CommandArgument( 0, "<BUNDLE>" )]
	[Description( "Bundle archive file or directory" )]
	public string Bundle { get; init; } = null!;

	[CommandOption( "-d|--driver <NAME>" )]
	[Description(
		"Database driver; its parameters are passed as --<driver>-<parameter> <value>. " +
		"See 'drivers list' and 'drivers parameters <driver>'"
	)]
	public string? Driver { get; init; }

	[CommandOption( "-t|--bundle-type <TYPE>" )]
	[Description( "Bundle type: zip, tar, tar.gz or directory; inferred from the bundle path when omitted" )]
	public string? BundleType { get; init; }

	[CommandOption( "--exit-code" )]
	[Description( "Exit 81 when deploying the bundle would deploy something, rather than 0" )]
	public bool ExitCode { get; init; }

	public override ValidationResult Validate()
	{
		if( string.IsNullOrEmpty( Driver ) )
			return ValidationResult.Error( "--driver is required; 'drivers list' names the ones this host offers" );

		return base.Validate();
	}
}

/// <summary>
/// Says what 'deploy' would do with a bundle against a database, without changing it: where the
/// journal is, and which plans would deploy, by release, in order — or that there is nothing to
/// deploy, or why the bundle does not fit (<see cref="IncompatibleBundleException"/>, exit 23). Only
/// the journal's queries run, in a transaction rolled back afterwards; never PrepareJournal, which is
/// why the bundle must carry the optional JournalExists statement, checked before any driver exists.
///
/// Failures are left to the central handler: nothing here can have changed the database, so the
/// code an exception earns needs nothing only this command knows.
/// </summary>
public sealed class StatusCommand(
	IAnsiConsole console,
	IDatabaseDriverRegistry drivers,
	IBundleTypeRegistry bundleTypes,
	IBundleLayoutRegistry layouts
)
	: Command<StatusSettings>
{
	protected override int Execute( CommandContext context, StatusSettings settings, CancellationToken cancellationToken )
	{
		IBundleType type = DeploymentTarget.ResolveBundleType( bundleTypes, settings.Bundle, settings.BundleType );

		using IBundleReader reader = type.Open( settings.Bundle );

		ValidatedBundle validated = BundleValidator.Validate( reader, layouts );

		// refused before a driver exists: without it a database never deployed to cannot be told from
		// a journal query that failed, and a status that guessed would mislead a production decision
		if( !validated.Journaling.IsSet( JournalingSlot.JournalExists ) )
			throw new JournalingStatementsNotSetException(
				[ nameof(JournalingSlot.JournalExists) ],
				DeploymentForecaster.JournalExistsPurpose
			);

		using IDatabaseDriver driver =
			DeploymentTarget.CreateDriver( drivers, settings.Driver!, context.Remaining, Timeout.InfiniteTimeSpan );

		DeploymentStatus status = new DeploymentForecaster( driver, validated.Journaling )
			.Forecast( validated.Manifest.Project.Name, validated.Releases );

		Write( status, settings.Format );

		return settings.ExitCode && !status.Forecast.NothingToDeploy ?
			DeployExitCodes.DeploymentPending :
			DeployExitCodes.Success;
	}

	private void Write( DeploymentStatus status, OutputFormat format )
	{
		DeploymentForecast forecast = status.Forecast;
		string state = State( status );
		string release = forecast.Journaled?.Name ?? "";

		List<(string Release, string Plan)> plans = forecast.Releases
			.SelectMany( planned => planned.PlansToDeploy.Select( plan => ( planned.Group.Name, plan.Name ) ) )
			.ToList();

		bool interactive = format == OutputFormat.Auto && console.Profile.Capabilities.Interactive;

		if( interactive )
			console.WriteLine( Describe( status ) );

		ResultWriter.WriteSections(
			console,
			format,
			new
			{
				database = new { state, release = forecast.Journaled?.Name },
				continuingFrom = forecast.ContinuingFrom,
				upToDate = forecast.NothingToDeploy,
				releases = forecast.Releases
					.Select( planned => new
						{
							release = planned.Group.Name,
							resumed = planned.Resumed,
							plans = planned.PlansToDeploy.Count()
						}
					)
					.ToList(),
				plans = plans.Select( plan => new { release = plan.Release, plan = plan.Plan } ).ToList()
			},
			[
				new ResultSection( "database", "Database", [ "State", "Release" ], [ [ state, release ] ] ),

				// a release is deployed even with no plan left to run — its completion is still journaled —
				// so the releases are listed apart from the plans, and '--exit-code' is never unexplained
				new ResultSection(
					"releases",
					"Releases to deploy",
					[ "Release", "Resumed", "Plans" ],
					forecast.Releases
						.Select( planned => new[]
							{
								planned.Group.Name,
								planned.Resumed ? "yes" : "no",
								planned.PlansToDeploy.Count().ToString()
							}
						)
						.ToList()
				),
				new ResultSection(
					"plans",
					"To deploy",
					[ "Release", "Plan" ],
					plans.Select( plan => new[] { plan.Release, plan.Plan } ).ToList()
				)
			]
		);

		if( !interactive )
			return;

		if( forecast.NothingToDeploy )
			console.WriteLine( "nothing to deploy" );
		else
			foreach( PlannedRelease planned in forecast.Releases.Where( x => !x.PlansToDeploy.Any() ) )
				// no plan to run, but the release's completion is still journaled
				console.WriteLine(
					planned.Resumed ?
						$"would complete {Name( planned.Group.Name )}, whose plans are all deployed" :
						$"would deploy {Name( planned.Group.Name )}, which has no plans"
				);
	}

	/// <summary>
	/// 'fresh': no journal, never deployed to. 'empty': a journal that holds nothing. 'partial' and
	/// 'complete': the journal is at a release, deployed partly or completely.
	/// </summary>
	private static string State( DeploymentStatus status )
		=> !status.JournalExists ? "fresh" :
			status.Forecast.Journaled is null ? "empty" :
			status.Forecast.Journaled.Completed ? "complete" : "partial";

	private static string Describe( DeploymentStatus status )
		=> status.Forecast.Journaled switch
		{
			_ when !status.JournalExists => "fresh database (no journal)",
			null => "journal exists but holds no release",
			{ Completed: true } journaled => $"database is at {Name( journaled.Name )}, fully deployed",
			{ } journaled => $"database is at {Name( journaled.Name )}, partly deployed"
		};

	private static string Name( string release ) => release.Length == 0 ? "unreleased plans" : $"release '{release}'";
}
