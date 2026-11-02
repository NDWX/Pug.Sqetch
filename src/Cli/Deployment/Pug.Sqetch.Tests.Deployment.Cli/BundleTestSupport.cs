using System.Text;

using Pug.Sqetch.Deployment.Bundling;
using Pug.Sqetch.Deployment.Bundling.Layouts;
using Pug.Sqetch.Planning.Models;
using Pug.Sqetch.Tests.Deployment;

namespace Pug.Sqetch.Tests.Deployment.Cli;

public sealed class InMemoryBundleReader : IBundleReader
{
	private readonly Dictionary<string, string> _entries = new();

	public InMemoryBundleReader Add( string path, string content )
	{
		_entries[path] = content;

		return this;
	}

	public InMemoryBundleReader Remove( string path )
	{
		_entries.Remove( path );

		return this;
	}

	public IEnumerable<string> Entries => _entries.Keys;

	public bool Contains( string path ) => _entries.ContainsKey( path );

	public Stream Open( string path )
		=> _entries.TryGetValue( path, out string? content ) ?
			new MemoryStream( Encoding.UTF8.GetBytes( content ) ) :
			throw new BundlingException( $"Bundle has no entry '{path}'." );

	public void Dispose()
	{
	}
}

public static class Manifests
{
	public static BundleManifestStep Step( string name ) => new( name, "", [ ] );

	public static BundleManifestPlan Plan( string name, string release, params string[] steps )
		=> new( name, "", release, [ ], steps.Select( Step ).ToList() );

	public static BundleManifestRelease Release( string name, string dependency = "" )
		=> new( name, "", dependency, true );

	public static BundleManifest Manifest(
		IReadOnlyList<BundleManifestRelease> releases,
		IReadOnlyList<BundleManifestPlan> plans
	)
		=> new(
			new BundleManifestProject( "demo", "", "postgres" ),
			BundleSelection.Finalized,
			releases,
			plans
		);

	/// <summary>Journaling SQL for every slot; see <see cref="FakeJournal.Statements"/>.</summary>
	public static JournalingStatements Journaling() => FakeJournal.Statements();

	/// <summary>The instant the in-memory bundles claim to have been built at.</summary>
	public static DateTime Bundled { get; } = new( 2026, 10, 17, 3, 15, 42, DateTimeKind.Utc );

	/// <summary>The metadata a bundle written through <paramref name="layout"/> would carry.</summary>
	public static BundleMetadata Metadata( IBundleLayout layout )
		=> new( layout.Format, layout.Name, "sqetch-deploy", "0.1.0", Bundled );

	/// <summary>A registry holding the built-in layouts, as the deployment host's does.</summary>
	public static BundleLayoutRegistry Layouts()
	{
		BundleLayoutRegistry layouts = new();

		layouts.RegisterLayouts();

		return layouts;
	}

	/// <summary>
	/// A reader holding the metadata and all three scripts for every step of the manifest, plus the
	/// journaling SQL — everything but the manifest itself, which a test adds in the shape it needs.
	/// </summary>
	public static InMemoryBundleReader ReaderFor( BundleManifest manifest, IBundleLayout layout )
	{
		InMemoryBundleReader reader = new();

		reader.Add( BundleMetadata.EntryName, Metadata( layout ).ToText() );

		foreach( BundleManifestPlan plan in manifest.Plans )
			foreach( BundleManifestStep step in plan.Steps )
				foreach( StepScriptKind kind in Enum.GetValues<StepScriptKind>() )
					reader.Add(
						layout.ScriptPath( plan.Name, step.Name, kind ),
						$"-- {kind.ToString().ToLowerInvariant()} {plan.Name}/{step.Name}"
					);

		foreach( JournalingSlot slot in JournalingSlots.All )
			reader.Add( $"journaling/{JournalingSlots.FileName( slot )}", FakeJournal.Marker( slot ) );

		return reader;
	}
}