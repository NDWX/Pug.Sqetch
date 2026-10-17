using System.ComponentModel;
using Pug.Sqetch.Bundling;
using Pug.Sqetch.Bundling.BundleTypes;
using Pug.Sqetch.Bundling.Layouts;
using Pug.Sqetch.Cli.Commands.Authoring;
using Pug.Sqetch.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Pug.Sqetch.Cli.Commands.Deployment;

/// <summary>
/// Packages a project's deployable plans and its journaling SQL into a bundle for 'deploy'. Only
/// reads the project, so unlike authoring it needs no project user.
/// </summary>
public sealed class BundleCommand(
	IAnsiConsole console,
	IBundleTypeRegistry bundleTypes,
	IBundleLayoutRegistry layouts,
	BundlerIdentity bundler )
	: Command<BundleCommand.Settings>
{
	public sealed class Settings : CommandSettings
	{
		[CommandOption( "--project <DIR>" )]
		[Description( "The project directory; defaults to the current directory" )]
		public string? Project { get; init; }

		[CommandOption( "--test" )]
		[Description( "Bundle every plan regardless of release status" )]
		public bool Test { get; init; }

		[CommandOption( "--finalized" )]
		[Description( "Bundle only plans of finalized releases (the default)" )]
		public bool Finalized { get; init; }

		[CommandOption( "-t|--bundle-type <TYPE>" )]
		[Description( "Bundle type: zip (default), tar, tar.gz or directory" )]
		public string? BundleType { get; init; }

		[CommandOption( "-o|--bundle-output-path <PATH>" )]
		[Description( "Output archive file or directory; defaults to <project>-<timestamp> in the current directory" )]
		public string? BundleOutputPath { get; init; }

		[CommandOption( "--since <RELEASE>" )]
		[Description( "Bundle only the releases after the named one (a continuation bundle)" )]
		public string? Since { get; init; }

		internal BundleSelection Selection => Test ? BundleSelection.Test : BundleSelection.Finalized;

		public override ValidationResult Validate()
		{
			if( Test && Finalized )
				return ValidationResult.Error( "specify either --test or --finalized, not both" );

			if( !NameValidation.IsValid( Since ) )
				return ValidationResult.Error( NameValidation.Error( "--since" ) );

			return ValidationResult.Success();
		}
	}

	protected override int Execute( CommandContext context, Settings settings, CancellationToken cancellationToken )
	{
		// strict parsing is off app-wide, so a typo'd option would otherwise be swallowed
		RemainingArguments.RejectAll( context.Remaining );

		// an unknown name is the registry's own refusal, which already lists what it does know
		IBundleType type = bundleTypes.Create( settings.BundleType ?? ZipBundleType.TypeName );

		IBundleLayout layout = layouts.Create( DefaultBundleLayout.LayoutName );

		using ProjectSession session = ProjectSession.Open( Path.GetFullPath( settings.Project ?? "." ) );

		// assembling verifies every included plan's step scripts, so failures surface
		// before any output exists
		Bundle bundle = new BundleBuilder( session.ReadOnlyProject ).Assemble( settings.Selection, settings.Since );

		string path = settings.BundleOutputPath
					?? type.DefaultOutputName( $"{bundle.Project.Name}-{DateTime.Now:yyyyMMddHHmmss}" );

		IBundleWriter writer;

		try
		{
			writer = type.Create( path );
		}
		catch( Exception exception )
		{
			// nothing exists at the path that is ours, so nothing is deleted
			throw new BundleOutputException( path, exception );
		}

		try
		{
			using( writer )
				BundlePackager.Write( bundle, layout, writer, bundler.Name, bundler.Version, BundledInstant() );
		}
		catch( Exception exception )
		{
			DeleteOutput( path );

			throw new BundleOutputException( path, exception );
		}

		console.WriteLine( path );

		return DeployExitCodes.Success;
	}

	/// <summary>The current UTC instant to the whole second, which is all the metadata can say.</summary>
	private static DateTime BundledInstant()
	{
		DateTime now = DateTime.UtcNow;

		return new DateTime( now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc );
	}

	private static void DeleteOutput( string path )
	{
		// Create refused pre-existing targets, so whatever sits at the path is our partial
		// output
		if( File.Exists( path ) )
			File.Delete( path );
		else if( Directory.Exists( path ) )
			Directory.Delete( path, recursive: true );
	}
}
