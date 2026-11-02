using Pug.Sqetch.DatabaseDriver;

using Spectre.Console.Cli;

namespace Pug.Sqetch.Deployment.Cli.Commands;

internal static class DriverParameters
{
	/// <summary>
	/// Extracts the selected driver's '--&lt;driver&gt;-&lt;parameter&gt;' switches from the
	/// options the command tree did not recognize, validates them against the driver's
	/// parameter definition and checks that at least one of its required parameter
	/// groupings is fully provided. Values are keyed by the definition's canonical
	/// parameter name.
	/// </summary>
	public static IDictionary<string, string> Parse( IRemainingArguments remaining, IDatabaseDriverFactory factory )
	{
		string prefix = $"--{factory.Name}-";
		DatabaseDriverParametersDefinition definition = factory.GetParametersDefinition();
		List<DatabaseDriverParameterDefinition> known = definition.Parameters.ToList();

		Dictionary<string, string> values = new( StringComparer.OrdinalIgnoreCase );

		foreach( IGrouping<string, string?> option in remaining.Parsed )
		{
			if( !option.Key.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ) )
				throw new CommandLineException(
					$"unknown option '{option.Key}'; driver parameters are passed as {prefix}<parameter> <value>"
				);

			string name = option.Key[prefix.Length..];

			DatabaseDriverParameterDefinition? parameter =
				known.FirstOrDefault( x => string.Equals( x.Name, name, StringComparison.OrdinalIgnoreCase ) );

			if( parameter is null )
				throw new CommandLineException(
					$"driver '{factory.Name}' has no parameter '{name}'; available parameters: " +
					string.Join( ", ", known.Select( x => prefix + x.Name ) )
				);

			string?[] provided = option.ToArray();

			if( provided.Length > 1 )
				throw new CommandLineException( $"option '{option.Key}' is specified more than once" );

			if( string.IsNullOrEmpty( provided[0] ) )
				throw new CommandLineException( $"option '{option.Key}' requires a value" );

			values[parameter.Name] = provided[0]!;
		}

		if( remaining.Raw.Count > 0 )
			throw new CommandLineException(
				$"unexpected argument(s): {string.Join( " ", remaining.Raw )}"
			);

		ICollection<ICollection<string>> groupings = definition.RequiredParametersOptions;

		if( groupings.Count > 0 && !groupings.Any( grouping => grouping.All( values.ContainsKey ) ) )
			throw new CommandLineException(
				$"driver '{factory.Name}' requires " +
				( groupings.Count == 1 ? "parameters " : "one of the parameter sets " ) +
				string.Join(
					" or ",
					groupings.Select( grouping => $"({string.Join( ", ", grouping.Select( x => prefix + x ) )})" )
				)
			);

		return values;
	}
}