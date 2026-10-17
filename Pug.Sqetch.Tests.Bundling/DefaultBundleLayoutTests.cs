using System.Text;
using System.Text.Json;
using Pug.Sqetch.Bundling;
using Pug.Sqetch.Bundling.Layouts;
using Pug.Sqetch.Models;

namespace Pug.Sqetch.Tests.Bundling;

public class DefaultBundleLayoutTests
{
	[Fact]
	public void WritesManifestFirstThenScriptsUnderPlansInBundleOrder()
	{
		TrackingStream deploy = Script( "-- deploy" ), verify = Script( "-- verify" ), rollback = Script( "-- rollback" );

		Bundle bundle = new (
			new ProjectDefinition( "demo", "Demo project", "postgres" ),
			BundleSelection.Finalized,
			Journaling(),
			[new BundleRelease( "2026.07", "July release", "", true )],
			[
				new BundlePlan(
					"table", "Create table", "2026.07", [],
					[new BundleStep( "create", "creates it", [], () => new StepScripts( deploy, verify, rollback ) )] ),
				new BundlePlan(
					"index", "Create index", "2026.07", ["table"],
					[new BundleStep( "add", "adds it", ["create"], () => new StepScripts( Script( "-- i" ), Script( "-- v" ), Script( "-- r" ) ) )] )
			] );

		RecordingWriter writer = new ();

		new DefaultBundleLayout().Write( bundle, writer );

		Assert.Equal(
			[
				"manifest.json",
				"journaling/OnDeployingRelease.sql",
				"journaling/OnDeployingPlan.sql",
				"journaling/OnDeployingStep.sql",
				"journaling/OnStepDeployed.sql",
				"journaling/OnPlanDeployed.sql",
				"journaling/OnReleaseDeployed.sql",
				"journaling/OnRollingBackRelease.sql",
				"journaling/OnRollingBackPlan.sql",
				"journaling/OnRollingBackStep.sql",
				"journaling/OnRolledBackStep.sql",
				"journaling/OnRolledBackPlan.sql",
				"journaling/OnRolledBackRelease.sql",
				"journaling/PrepareJournal.sql",
				"journaling/GetLatestRelease.sql",
				"journaling/GetDeployedPlans.sql",
				"plans/table/steps/create/deploy.sql",
				"plans/table/steps/create/verify.sql",
				"plans/table/steps/create/rollback.sql",
				"plans/index/steps/add/deploy.sql",
				"plans/index/steps/add/verify.sql",
				"plans/index/steps/add/rollback.sql"
			],
			writer.Entries.Select( x => x.Path ).ToArray() );

		// entry 0 is the manifest and 1..15 the journaling slots, so the first script is 16
		Assert.Equal( "-- deploy", writer.Entries[16].Content );

		// each step's scripts are disposed right after they are written
		Assert.True( deploy.Disposed );
		Assert.True( verify.Disposed );
		Assert.True( rollback.Disposed );

		using JsonDocument manifest = JsonDocument.Parse( writer.Entries[0].Content );
		JsonElement rootElement = manifest.RootElement;

		Assert.Equal( "demo", rootElement.GetProperty( "project" ).GetProperty( "name" ).GetString() );
		Assert.Equal( "postgres", rootElement.GetProperty( "project" ).GetProperty( "engine" ).GetString() );
		Assert.Equal( "finalized", rootElement.GetProperty( "selection" ).GetString() );

		JsonElement release = rootElement.GetProperty( "releases" )[0];
		Assert.Equal( "2026.07", release.GetProperty( "name" ).GetString() );
		Assert.True( release.GetProperty( "finalized" ).GetBoolean() );

		JsonElement[] plans = rootElement.GetProperty( "plans" ).EnumerateArray().ToArray();
		Assert.Equal( ["table", "index"], plans.Select( x => x.GetProperty( "name" ).GetString() ).ToArray() );
		Assert.Equal( "2026.07", plans[1].GetProperty( "release" ).GetString() );
		Assert.Equal( "table", plans[1].GetProperty( "dependencies" )[0].GetString() );
		Assert.Equal( "add", plans[1].GetProperty( "steps" )[0].GetProperty( "name" ).GetString() );
		Assert.Equal( "create", plans[1].GetProperty( "steps" )[0].GetProperty( "dependencies" )[0].GetString() );
	}

	/// <summary>
	/// The metadata names the layout, so it is written ahead of the layout by the packager; a layout
	/// that wrote it too would leave a bundle with two.
	/// </summary>
	[Fact]
	public void TheLayoutLeavesTheMetadataEntryToThePackagerAndWritesNoGeneratedTimestamp()
	{
		RecordingWriter writer = new ();

		new DefaultBundleLayout().Write(
			new Bundle( new ProjectDefinition( "demo", "", "" ), BundleSelection.Finalized, Journaling(), [], [] ),
			writer );

		Assert.DoesNotContain( writer.Entries, x => x.Path == BundleMetadata.EntryName );
		Assert.DoesNotContain( "generated", writer.Entries[0].Content );
		Assert.Equal( 1, new DefaultBundleLayout().Format );
	}

	[Fact]
	public void NullScriptStreamsAreSkippedButTheStepStaysInTheManifest()
	{
		Bundle bundle = new (
			new ProjectDefinition( "demo", "", "postgres" ),
			BundleSelection.Test,
			Journaling(),
			[],
			[
				new BundlePlan(
					"partial", "", "", [],
					[new BundleStep( "only-deploy", "", [], () => new StepScripts( Script( "-- d" ), null, null ) )] )
			] );

		RecordingWriter writer = new ();

		new DefaultBundleLayout().Write( bundle, writer );

		Assert.Equal(
			["manifest.json", "plans/partial/steps/only-deploy/deploy.sql"],
			writer.Entries.Select( x => x.Path ).Where( x => !x.StartsWith( "journaling/" ) ).ToArray() );

		using JsonDocument manifest = JsonDocument.Parse( writer.Entries[0].Content );

		Assert.Equal( "test", manifest.RootElement.GetProperty( "selection" ).GetString() );
		Assert.Equal(
			"only-deploy",
			manifest.RootElement.GetProperty( "plans" )[0].GetProperty( "steps" )[0].GetProperty( "name" ).GetString() );
	}

	[Fact]
	public void WrittenManifestsReadBackAsTheSameTypedModel()
	{
		Bundle bundle = new (
			new ProjectDefinition( "demo", "Demo project", "postgres" ),
			BundleSelection.Test,
			Journaling(),
			[new BundleRelease( "2026.07", "July release", "2026.06", true )],
			[
				new BundlePlan(
					"table", "Create table", "2026.07", [],
					[new BundleStep( "create", "creates it", [], () => new StepScripts( Script( "-- d" ), Script( "-- v" ), Script( "-- r" ) ) )] ),
				new BundlePlan(
					"pending", "Unreleased", "", ["table"],
					[new BundleStep( "tweak", "", ["create"], () => new StepScripts( Script( "-- d" ), Script( "-- v" ), Script( "-- r" ) ) )] )
			] );

		RecordingWriter writer = new ();
		DefaultBundleLayout layout = new ();

		layout.Write( bundle, writer );

		BundleManifest manifest = layout.ReadManifest( new FakeReader( writer ) );

		Assert.Equal( new BundleManifestProject( "demo", "Demo project", "postgres" ), manifest.Project );
		Assert.Equal( BundleSelection.Test, manifest.Selection );
		Assert.Equal( [new BundleManifestRelease( "2026.07", "July release", "2026.06", true )], manifest.Releases );
		Assert.Equal( ["table", "pending"], manifest.Plans.Select( x => x.Name ).ToArray() );
		Assert.Equal( "", manifest.Plans[1].Release );
		Assert.Equal( ["table"], manifest.Plans[1].Dependencies );
		Assert.Equal( ["create"], manifest.Plans[1].Steps[0].Dependencies );
	}

	/// <summary>
	/// Journaling entries sit between the manifest and the plan payload: a deployment needs them
	/// before it may run any script, and the layout's doc comment states that order as a contract.
	/// </summary>
	[Fact]
	public void JournalingStatementsAreWrittenAndReadBackThroughTheLayout()
	{
		Bundle bundle = new (
			new ProjectDefinition( "demo", "", "postgres" ),
			BundleSelection.Test,
			Journaling(),
			[],
			[
				new BundlePlan(
					"p", "", "", [],
					[new BundleStep( "s", "", [], () => new StepScripts( Script( "-- d" ), null, null ) )] )
			] );

		RecordingWriter writer = new ();
		DefaultBundleLayout layout = new ();

		layout.Write( bundle, writer );

		List<string> paths = writer.Entries.Select( x => x.Path ).ToList();

		Assert.Equal( 15, paths.Count( x => x.StartsWith( "journaling/" ) ) );
		Assert.Equal( 0, paths.IndexOf( "manifest.json" ) );
		Assert.True(
			paths.FindLastIndex( x => x.StartsWith( "journaling/" ) )
			< paths.FindIndex( x => x.StartsWith( "plans/" ) ) );

		JournalingStatements read = layout.ReadJournalingStatements( new FakeReader( writer ) );

		Assert.Equal( "-- PrepareJournal", read.Text( JournalingSlot.PrepareJournal ) );
		Assert.Equal( ["-- GetDeployedPlans"], read.Statements( JournalingSlot.GetDeployedPlans ) );
	}

	[Fact]
	public void AMissingJournalingSlotIsRefusedByName()
	{
		BundlingException error = Assert.Throws<BundlingException>(
			() => new DefaultBundleLayout().ReadJournalingStatements( new FakeReader() ) );

		Assert.Contains( "journaling/OnDeployingRelease.sql", error.Message );
		Assert.Contains( "journaling/GetDeployedPlans.sql", error.Message );
	}

	private static JournalingStatements Journaling()
		=> new ( JournalingSlots.All.ToDictionary( slot => slot, slot => (string?)$"-- {slot}" ) );

	[Fact]
	public void MissingManifestIsRefused()
	{
		InvalidBundleManifestException error = Assert.Throws<InvalidBundleManifestException>(
			() => new DefaultBundleLayout().ReadManifest( new FakeReader() ) );

		Assert.Contains( "manifest.json", error.Message );
	}

	[Theory]
	[InlineData( "not json at all" )]
	[InlineData( "null" )]
	[InlineData( """{ "plans": [] }""" )]
	[InlineData( """{ "project": { "name": "demo" } }""" )]
	[InlineData( """{ "project": { "name": "demo" }, "plans": [ { "description": "unnamed" } ] }""" )]
	[InlineData( """{ "project": { "name": "demo" }, "plans": [ { "name": "p", "steps": [ {} ] } ] }""" )]
	[InlineData( """{ "project": { "name": "demo" }, "plans": [], "releases": [ {} ] }""" )]
	public void InvalidManifestsAreRefused( string manifest )
	{
		Assert.Throws<InvalidBundleManifestException>(
			() => new DefaultBundleLayout().ReadManifest( new FakeReader( manifest ) ) );
	}

	[Fact]
	public void AbsentOptionalManifestMembersFallBackToEmptyValues()
	{
		BundleManifest manifest = new DefaultBundleLayout().ReadManifest( new FakeReader(
			"""
			{
				"project": { "name": "demo" },
				"releases": [ { "name": "2026.07" }, { "name": "2026.08" } ],
				"plans": [ { "name": "table", "steps": [ { "name": "create" } ] } ]
			}
			""" ) );

		Assert.Equal( new BundleManifestProject( "demo", "", "" ), manifest.Project );
		Assert.Equal( BundleSelection.Finalized, manifest.Selection );

		// releases without a 'dependency' member normalize positionally: the first falls
		// back to "", each following one to the preceding release's name
		Assert.Equal( ["", "2026.07"], manifest.Releases.Select( x => x.Dependency ).ToArray() );

		BundleManifestPlan plan = Assert.Single( manifest.Plans );

		Assert.Equal( "", plan.Description );
		Assert.Equal( "", plan.Release );
		Assert.Empty( plan.Dependencies );
		Assert.Empty( Assert.Single( plan.Steps ).Dependencies );
	}

	[Fact]
	public void ScriptPathsFollowThePlansTree()
	{
		DefaultBundleLayout layout = new ();

		Assert.Equal( "plans/table/steps/create/deploy.sql", layout.ScriptPath( "table", "create", StepScriptKind.Deploy ) );
		Assert.Equal( "plans/table/steps/create/verify.sql", layout.ScriptPath( "table", "create", StepScriptKind.Verify ) );
		Assert.Equal( "plans/table/steps/create/rollback.sql", layout.ScriptPath( "table", "create", StepScriptKind.Rollback ) );
	}

	private static TrackingStream Script( string content ) => new ( Encoding.UTF8.GetBytes( content ) );

	private sealed class FakeReader : IBundleReader
	{
		private readonly Dictionary<string, string> _entries = new ();

		public FakeReader()
		{
		}

		public FakeReader( string manifest ) => _entries["manifest.json"] = manifest;

		public FakeReader( RecordingWriter writer )
		{
			foreach( (string path, string content) in writer.Entries )
				_entries[path] = content;
		}

		public IEnumerable<string> Entries => _entries.Keys;

		public bool Contains( string path ) => _entries.ContainsKey( path );

		public Stream Open( string path )
			=> _entries.TryGetValue( path, out string? content )
				? new MemoryStream( Encoding.UTF8.GetBytes( content ) )
				: throw new BundlingException( $"Bundle has no entry '{path}'." );

		public void Dispose()
		{
		}
	}

	private sealed class RecordingWriter : IBundleWriter
	{
		public List<(string Path, string Content)> Entries { get; } = [];

		public void Add( string path, Stream content )
		{
			using MemoryStream buffer = new ();

			content.CopyTo( buffer );

			Entries.Add( (path, Encoding.UTF8.GetString( buffer.ToArray() )) );
		}

		public void Dispose()
		{
		}
	}

	private sealed class TrackingStream( byte[] data ) : MemoryStream( data )
	{
		public bool Disposed { get; private set; }

		protected override void Dispose( bool disposing )
		{
			Disposed = true;

			base.Dispose( disposing );
		}
	}
}
