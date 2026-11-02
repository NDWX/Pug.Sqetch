using System.Globalization;

using Pug.Sqetch.Planning.Models;

namespace Pug.Sqetch.Cli.Commands.Authoring.Output;

/// <summary>Row projections shared by the table, TSV and CSV renderings.</summary>
internal static class Rows
{
	public static readonly string[] PlanHeaders =
	[
		"Name", "Release", "Created", "Created By", "Depends On", "Description"
	];

	public static readonly string[] ReleaseHeaders =
	[
		"Name", "State", "Created", "Created By", "Finalized", "Finalized By", "Depends On", "Description"
	];

	public static readonly string[] StepHeaders = [ "Name", "Created", "Created By", "Depends On", "Description" ];

	public static string[] Plan( ProjectPlan plan )
		=>
		[
			plan.Definition.Name,
			plan.Release.Length == 0 ? "(unreleased)" : plan.Release,
			Timestamp( plan.Registration.Timestamp ),
			plan.Registration.Subject.EmailAddress,
			Dependencies( ( plan.Definition as PlanDefinition )?.Dependencies ),
			plan.Definition.Description
		];

	public static string[] Release( ProjectRelease release )
		=>
		[
			release.Definition.Name,
			release.Finalized is null ? "open" : "finalized",
			Timestamp( release.Registration.Timestamp ),
			release.Registration.Subject.EmailAddress,
			release.Finalized is null ? "" : Timestamp( release.Finalized.Timestamp ),
			release.Finalized?.Subject.EmailAddress ?? "",
			release.Definition.Dependency,
			release.Definition.Description
		];

	public static string[] Step( ProjectElement step )
		=>
		[
			step.Definition.Name,
			Timestamp( step.Registration.Timestamp ),
			step.Registration.Subject.EmailAddress,
			Dependencies( ( step.Definition as StepDefinition )?.Dependencies ),
			step.Definition.Description
		];

	// explicit JSON shapes: 'Definition' is declared as the base ObjectDefinition on the
	// models, so serializing them directly would drop derived fields like dependencies

	public static object PlanJson( ProjectPlan plan )
		=> new
		{
			name = plan.Definition.Name,
			description = plan.Definition.Description,
			release = plan.Release,
			dependencies = ( plan.Definition as PlanDefinition )?.Dependencies ?? [ ],
			registration = plan.Registration
		};

	public static object ReleaseJson( ProjectRelease release )
		=> new
		{
			name = release.Definition.Name,
			description = release.Definition.Description,
			dependency = release.Definition.Dependency,
			registration = release.Registration,
			finalized = release.Finalized
		};

	public static object StepJson( ProjectElement step )
		=> new
		{
			name = step.Definition.Name,
			description = step.Definition.Description,
			dependencies = ( step.Definition as StepDefinition )?.Dependencies ?? [ ],
			registration = step.Registration
		};

	public static string Timestamp( DateTime value )
		=> value.ToString( "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture );

	public static string Dependencies( IEnumerable<string>? dependencies )
		=> dependencies is null ? "" : string.Join( ';', dependencies );
}