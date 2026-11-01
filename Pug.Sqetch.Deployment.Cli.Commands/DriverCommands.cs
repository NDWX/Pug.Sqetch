using System.ComponentModel;
using Pug.Sqetch.Cli;
using Pug.Sqetch.DatabaseDriver;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Pug.Sqetch.Deployment.Cli.Commands;

/// <summary>
/// The names '--driver' accepts, as this host registered them. Answers from the driver registry
/// alone, so unlike every other command it needs neither a bundle nor a database — which is the
/// point: whoever is reaching for a driver name has usually got neither to hand yet.
/// </summary>
public sealed class DriverListCommand( IAnsiConsole console, IDatabaseDriverRegistry drivers )
	: Command<DriverListCommand.Settings>
{
	public sealed class Settings : OutputSettings;

	protected override int Execute( CommandContext context, Settings settings, CancellationToken cancellationToken )
	{
		RemainingArguments.RejectAll( context.Remaining );

		// the registry keys by insertion, which is the host's wiring order rather than anything a
		// reader can predict, so the listing is sorted
		List<string> names = drivers.Names.OrderBy( x => x, StringComparer.OrdinalIgnoreCase ).ToList();

		ResultWriter.WriteRows(
			console, settings.Format, ["Driver"], names,
			x => [x],
			x => new { driver = x } );

		return DeployExitCodes.Success;
	}
}

/// <summary>
/// A driver's switches. The required sets come first and the parameter descriptions after, because
/// which parameters a driver needs is a fact about *sets* of them — 'host' and 'database' together,
/// or 'connection-string' on its own — and nothing per-parameter can say that: a column marking
/// each one required or not would turn a choice between two ways of connecting into seven
/// independent flags. So the sets are the answer to "what must I pass", and the parameter list the
/// answer to "what does each one mean".
///
/// A driver with no required sets gets an empty first section rather than none, so the result has
/// the same shape whatever it finds.
/// </summary>
public sealed class DriverParametersCommand( IAnsiConsole console, IDatabaseDriverRegistry drivers )
	: Command<DriverParametersCommand.Settings>
{
	public sealed class Settings : OutputSettings
	{
		[CommandArgument( 0, "[driver]" )]
		[Description( "Show only this driver's parameters; every driver when omitted" )]
		public string? Driver { get; init; }
	}

	private sealed record Parameter( string Switch, string Description );

	private sealed record DriverView(
		string Driver, IReadOnlyList<IReadOnlyList<string>> RequiredSets, IReadOnlyList<Parameter> Parameters );

	protected override int Execute( CommandContext context, Settings settings, CancellationToken cancellationToken )
	{
		RemainingArguments.RejectAll( context.Remaining );

		// an unknown name is the registry's own refusal, which already lists what it does know
		List<DriverView> views = settings.Driver is null
			? drivers.Names
						.OrderBy( x => x, StringComparer.OrdinalIgnoreCase )
						.Select( name => View( drivers.Create( name ) ) )
						.ToList()
			: [View( drivers.Create( settings.Driver ) )];

		ResultWriter.WriteSections(
			console, settings.Format,
			views.Select(
					view => new
					{
						driver = view.Driver,
						requiredParameterSets = view.RequiredSets,
						parameters = view.Parameters.Select( x => new { parameter = x.Switch, description = x.Description } )
					} )
				.ToList(),
			views.SelectMany( Sections ).ToList() );

		return DeployExitCodes.Success;
	}

	/// <summary>
	/// Shown as the switch a caller actually types rather than the bare parameter name the driver
	/// contract uses, the way 'sqetch journaling parameters' shows '@project' rather than 'project'.
	/// </summary>
	private static DriverView View( IDatabaseDriverFactory factory )
	{
		string prefix = $"--{factory.Name}-";
		DatabaseDriverParametersDefinition definition = factory.GetParametersDefinition();

		return new DriverView(
			factory.Name,
			definition.RequiredParametersOptions
						.Select( IReadOnlyList<string> ( grouping ) => grouping.Select( x => prefix + x ).ToList() )
						.ToList(),
			definition.Parameters
						.Select( x => new Parameter( prefix + x.Name, x.Description ) )
						.ToList() );
	}

	/// <summary>
	/// Two sections per driver, keyed by driver name so that listing every driver stays readable
	/// piped — a row carries which driver and which section it came from.
	/// </summary>
	private static IEnumerable<ResultSection> Sections( DriverView view )
	{
		yield return new ResultSection(
			$"{view.Driver}/required-sets",
			$"{view.Driver}: required parameter sets",
			["Set", "Parameters"],
			view.RequiredSets
				.Select( ( set, index ) => new[] { ( index + 1 ).ToString(), string.Join( " ", set ) } )
				.ToList() );

		yield return new ResultSection(
			$"{view.Driver}/parameters",
			$"{view.Driver}: parameters",
			["Parameter", "Description"],
			view.Parameters.Select( x => new[] { x.Switch, x.Description } ).ToList() );
	}
}
