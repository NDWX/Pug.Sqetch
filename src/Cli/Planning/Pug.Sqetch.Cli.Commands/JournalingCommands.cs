using System.ComponentModel;

using Pug.Sqetch.Planning.Models;

using Spectre.Console;
using Spectre.Console.Cli;

namespace Pug.Sqetch.Cli.Commands.Authoring;

/// <summary>
/// A '&lt;SLOT&gt;' positional argument shared by 'print' and 'set', resolved case-insensitively
/// through <see cref="JournalingSlots.TryParse"/> at validation time so a bad name is reported
/// before the project is even opened.
/// </summary>
internal static class JournalingSlotArgument
{
	public static ValidationResult Resolve( string? name, out JournalingSlot slot )
	{
		if( JournalingSlots.TryParse( name, out slot ) )
			return ValidationResult.Success();

		return ValidationResult.Error(
			$"'{name}' is not a journaling slot; valid slots are: {string.Join( ", ", JournalingSlots.Names )}"
		);
	}
}

public sealed class JournalingListCommand( IAnsiConsole console ) : Command<JournalingListCommand.Settings>
{
	public sealed class Settings : OutputSettings
	{
		[CommandOption( "--statements" )]
		[Description( "Include each slot's SQL as a further column" )]
		public bool Statements { get; init; }
	}

	private readonly record struct Row( JournalingSlot Slot, bool Set, bool Optional, string? Statement );

	protected override int Execute( CommandContext context, Settings settings, CancellationToken cancellationToken )
	{
		using ProjectSession session = ProjectSession.Open();

		List<Row> rows = JournalingSlots.All
			.Select( slot => {
					string? statement = session.Project.GetJournalingStatement( slot );

					return new Row( slot, statement is not null, JournalingSlots.IsOptional( slot ), statement );
				}
			)
			.ToList();

		// 'Optional' is appended, never inserted, so columns scripts already read keep their places; with
		// --statements it is left out of the tabular formats, since Statement must stay last — its SQL may
		// span lines, which would push any later column onto a line of its own. JSON carries it always
		string[] headers = settings.Statements ? [ "Slot", "Set", "Statement" ] : [ "Slot", "Set", "Optional" ];

		Func<Row, string[]> row = settings.Statements ?
			x => [ x.Slot.ToString(), x.Set ? "yes" : "no", x.Statement ?? "" ] :
			x => [ x.Slot.ToString(), x.Set ? "yes" : "no", x.Optional ? "yes" : "no" ];

		Func<Row, object> json = settings.Statements ?
			x => new { slot = x.Slot.ToString(), set = x.Set, optional = x.Optional, statement = x.Statement } :
			x => new { slot = x.Slot.ToString(), set = x.Set, optional = x.Optional };

		ResultWriter.WriteRows( console, settings.Format, headers, rows, row, json );

		return 0;
	}
}

/// <summary>
/// Which parameters a slot's statements may use. Answers from the contract alone, so unlike the
/// other journaling commands it needs no project — a maintainer can consult it before 'project init'
/// and from anywhere.
/// </summary>
public sealed class JournalingParametersCommand( IAnsiConsole console )
	: Command<JournalingParametersCommand.Settings>
{
	public sealed class Settings : OutputSettings
	{
		[CommandArgument( 0, "[slot]" )]
		[Description( "Show only this slot's parameters; every slot when omitted" )]
		public string? SlotName { get; init; }

		internal JournalingSlot? Slot { get; private set; }

		public override ValidationResult Validate()
		{
			if( SlotName is not null )
			{
				ValidationResult result = JournalingSlotArgument.Resolve( SlotName, out JournalingSlot resolved );

				if( !result.Successful )
					return result;

				Slot = resolved;
			}

			// base parses --output; returning early above would silently ignore it
			return base.Validate();
		}
	}

	private readonly record struct Row( JournalingSlot Slot, IReadOnlyList<string> Parameters );

	protected override int Execute( CommandContext context, Settings settings, CancellationToken cancellationToken )
	{
		// shown with the '@' a maintainer actually types; the contract itself names them bare,
		// because adding any provider prefix is the driver's job
		List<Row> rows = JournalingSlots.All
			.Where( slot => settings.Slot is null || slot == settings.Slot )
			.Select( slot => new Row(
					slot,
					JournalingSlots.ParameterNames( slot )
						.Select( name => $"@{name}" )
						.ToList()
				)
			)
			.ToList();

		ResultWriter.WriteRows(
			console,
			settings.Format,
			[ "Slot", "Parameters" ],
			rows,
			x => [ x.Slot.ToString(), string.Join( " ", x.Parameters ) ],
			x => new { slot = x.Slot.ToString(), parameters = x.Parameters }
		);

		return 0;
	}
}

public sealed class JournalingPrintCommand( IAnsiConsole console ) : Command<JournalingPrintCommand.Settings>
{
	public sealed class Settings : CommandSettings
	{
		[CommandArgument( 0, "<slot>" )] public string SlotName { get; init; } = "";

		internal JournalingSlot Slot { get; private set; }

		public override ValidationResult Validate()
		{
			ValidationResult result = JournalingSlotArgument.Resolve( SlotName, out JournalingSlot resolved );

			Slot = resolved;

			return result;
		}
	}

	protected override int Execute( CommandContext context, Settings settings, CancellationToken cancellationToken )
	{
		using ProjectSession session = ProjectSession.Open();

		string? statement = session.Project.GetJournalingStatement( settings.Slot );

		if( statement is null )
			throw new InvalidOperationException(
				$"journaling statement '{settings.Slot}' is not set; run 'sqetch journaling set {settings.Slot} ...'"
			);

		// verbatim and off IAnsiConsole markup: MarkupLineInterpolated would mangle SQL
		// containing '[', and 'journaling print X > x.sql' must round-trip byte for byte
		console.WriteLine( statement );

		return 0;
	}
}

public sealed class JournalingSetCommand : Command<JournalingSetCommand.Settings>
{
	public sealed class Settings : CommandSettings
	{
		[CommandArgument( 0, "<slot>" )] public string SlotName { get; init; } = "";

		[CommandArgument( 1, "[sql]" )] public string? Sql { get; init; }

		[CommandOption( "--file <PATH>" )]
		[Description( "Read the statement text from this file" )]
		public string? FilePath { get; init; }

		[CommandOption( "--stdin" )]
		[Description( "Read the statement text from standard input, to EOF" )]
		public bool Stdin { get; init; }

		internal JournalingSlot Slot { get; private set; }

		public override ValidationResult Validate()
		{
			ValidationResult slot = JournalingSlotArgument.Resolve( SlotName, out JournalingSlot resolved );

			if( !slot.Successful )
				return slot;

			Slot = resolved;

			int sources = ( Sql is not null ? 1 : 0 ) + ( FilePath is not null ? 1 : 0 ) + ( Stdin ? 1 : 0 );

			if( sources != 1 )
				return ValidationResult.Error( "specify exactly one of <sql>, --file <PATH> or --stdin" );

			return ValidationResult.Success();
		}
	}

	protected override int Execute( CommandContext context, Settings settings, CancellationToken cancellationToken )
	{
		using ProjectSession session = ProjectSession.Open();

		// File.ReadAllText detects and strips a BOM, same as the store's own read
		string text = settings.Sql ??
					( settings.FilePath is not null ? File.ReadAllText( settings.FilePath ) : Console.In.ReadToEnd() );

		session.Project.SetJournalingStatement( settings.Slot, text );

		return 0;
	}
}
/// <summary>
/// Copies a journaling template into the project: every slot at once, from the repository level,
/// the user level or the driver's built-in template, whichever has the name first. The driver
/// defaults to the project's engine when that names one. From here on the project owns the SQL.
/// </summary>
public sealed class JournalingInitCommand(
	IAnsiConsole console,
	JournalingTemplateSources templates,
	WarningWriter warnings ) : Command<JournalingInitCommand.Settings>
{
	public sealed class Settings : OutputSettings
	{
		[CommandArgument( 0, "[driver]" )]
		[Description( "Database driver whose template to use; defaults to the project's engine when that names a driver" )]
		public string? Driver { get; init; }

		[CommandOption( "--template <NAME>" )]
		[Description( "Template to use instead of 'default'; see 'sqetch journaling templates'" )]
		public string? Template { get; init; }

		[CommandOption( "--force" )]
		[Description( "Replace journaling statements that are already set" )]
		public bool Force { get; init; }
	}

	protected override int Execute( CommandContext context, Settings settings, CancellationToken cancellationToken )
	{
		using ProjectSession session = ProjectSession.Open();

		string driver = Driver( session.Project.GetDefinition().Engine, settings.Driver );

		JournalingTemplate template = templates.Resolve(
			session.Root, driver, settings.Template ?? JournalingTemplateSources.DefaultTemplate );

		if( !settings.Force )
		{
			List<JournalingSlot> set = JournalingSlots.All
				.Where( slot => session.Project.GetJournalingStatement( slot ) is not null )
				.ToList();

			if( set.Count > 0 )
				throw new JournalingTemplateException(
					$"journaling statements are already set: {string.Join( ", ", set )}; pass --force to replace them all" );
		}

		// a template may lack an optional slot's file; it is copied when present
		List<JournalingSlot> slots = JournalingSlots.All.Where( template.Statements.ContainsKey ).ToList();

		// an optional slot the template lacks is either left unset — which a command needing it will
		// refuse later, so say so now — or, there being no unsetting a slot, keeps its old SQL, which may
		// have been written for other tables; neither is left unannounced
		List<JournalingSlot> lacking = JournalingSlots.All.Where( slot => !template.Statements.ContainsKey( slot ) ).ToList();
		List<JournalingSlot> kept = lacking.Where( slot => session.Project.GetJournalingStatement( slot ) is not null ).ToList();
		List<JournalingSlot> unset = lacking.Except( kept ).ToList();

		if( kept.Count > 0 )
			warnings.Write(
				$"the template does not set {string.Join( ", ", kept )}, so the project keeps its earlier SQL " +
				"for it; check it suits this template's journal, and change it with 'sqetch journaling set'" );

		if( unset.Count > 0 )
			warnings.Write(
				$"the template does not set the optional {string.Join( ", ", unset )}, so it stays unset; " +
				"'sqetch-deploy status' needs JournalExists: set it with 'sqetch journaling set'" );

		int written = 0;

		try
		{
			foreach( JournalingSlot slot in slots )
			{
				session.Project.SetJournalingStatement( slot, template.Statements[slot] );
				written++;
			}
		}
		catch( Exception exception ) when( written > 0 )
		{
			// the project store is not transactional, so the first slots are already replaced
			throw new JournalingTemplateException(
				$"stopped after setting {written} of {slots.Count} journaling statements " +
				$"({exception.Message}); fix the cause and rerun with --force" );
		}

		ResultWriter.WriteDetails(
			console,
			settings.Format,
			new
			{
				driver = template.Driver,
				template = template.Name,
				level = JournalingTemplateLevels.Describe( template.Level ),
				location = template.Location,
				slots = slots.Select( slot => slot.ToString() ).ToList()
			},
			[
				( "Driver", template.Driver ),
				( "Template", template.Name ),
				( "Level", JournalingTemplateLevels.Describe( template.Level ) ),
				( "Location", template.Location ?? "(built in)" ),
				( "Slots set", written.ToString() )
			]
		);

		return 0;
	}

	private string Driver( string engine, string? argument )
	{
		bool engineIsDriver = !string.IsNullOrWhiteSpace( engine ) && templates.IsDriver( engine );

		if( argument is null )
		{
			if( engineIsDriver )
				return templates.ResolveDriver( engine );

			string drivers = string.Join( ", ", templates.Drivers.Order( StringComparer.OrdinalIgnoreCase ) );

			throw new JournalingTemplateException(
				string.IsNullOrWhiteSpace( engine ) ?
					$"the project names no engine; pass a driver: {drivers}" :
					$"the project's engine '{engine}' names no database driver; pass one: {drivers}" );
		}

		string driver = templates.ResolveDriver( argument );

		if( engineIsDriver && !string.Equals( templates.ResolveDriver( engine ), driver, StringComparison.OrdinalIgnoreCase ) )
			warnings.Write( $"the project's engine is '{engine}', but this template is for driver '{driver}'" );

		return driver;
	}
}

/// <summary>
/// The journaling templates 'journaling init' can use: per driver, at the repository level, the
/// user level and built in, marking the ones a more preferred level shadows. Works outside a
/// project too, from the current directory.
/// </summary>
public sealed class JournalingTemplatesCommand(
	IAnsiConsole console,
	JournalingTemplateSources templates,
	WarningWriter warnings ) : Command<JournalingTemplatesCommand.Settings>
{
	public sealed class Settings : OutputSettings
	{
		[CommandArgument( 0, "[driver]" )]
		[Description( "Show only this driver's templates" )]
		public string? Driver { get; init; }
	}

	protected override int Execute( CommandContext context, Settings settings, CancellationToken cancellationToken )
	{
		string directory = Directory.GetCurrentDirectory();

		foreach( string folder in templates.UnknownDriverFolders( directory ) )
			warnings.Write( $"'{folder}' names no database driver and is ignored" );

		ResultWriter.WriteRows(
			console,
			settings.Format,
			[ "Driver", "Template", "Level", "Location", "Used" ],
			templates.List( directory, settings.Driver ),
			x => [
				x.Driver, x.Name, JournalingTemplateLevels.Describe( x.Level ), x.Location ?? "(built in)",
				x.Shadowed ? "shadowed" : "yes"
			],
			x => new
			{
				driver = x.Driver,
				template = x.Name,
				level = JournalingTemplateLevels.Describe( x.Level ),
				location = x.Location,
				shadowed = x.Shadowed
			}
		);

		return 0;
	}
}

internal static class JournalingTemplateLevels
{
	public static string Describe( JournalingTemplateLevel level )
		=> level switch
		{
			JournalingTemplateLevel.Repository => "repository",
			JournalingTemplateLevel.User => "user",
			_ => "built-in"
		};
}
