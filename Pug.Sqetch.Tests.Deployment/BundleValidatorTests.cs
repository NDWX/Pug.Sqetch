using Pug.Sqetch.Bundling;
using Pug.Sqetch.Bundling.Layouts;
using Pug.Sqetch.Deployment;
using Pug.Sqetch.Models;
using static Pug.Sqetch.Tests.Deployment.Manifests;

namespace Pug.Sqetch.Tests.Deployment;

public class BundleValidatorTests
{
	private readonly DefaultBundleLayout _layout = new ();

	[Fact]
	public void CompleteBundlesPassAndReturnTheMetadataLayoutManifestAndJournaling()
	{
		BundleManifest manifest = Manifest( [Release( "r1" )], [Plan( "a", "r1", "s1" )] );
		InMemoryBundleReader reader = ReaderFor( manifest, _layout );

		reader.Add( "manifest.json", ManifestJson( manifest ) );

		ValidatedBundle read = BundleValidator.Validate( reader, Layouts() );

		Assert.Equal( Metadata( _layout ), read.Metadata );
		Assert.IsType<DefaultBundleLayout>( read.Layout );
		Assert.Equal( "a", Assert.Single( read.Manifest.Plans ).Name );
		Assert.Equal(
			[FakeJournal.Marker( JournalingSlot.PrepareJournal )],
			read.Journaling.Statements( JournalingSlot.PrepareJournal ) );
		Assert.Equal(
			FakeJournal.Marker( JournalingSlot.GetLatestRelease ),
			read.Journaling.Text( JournalingSlot.GetLatestRelease ) );
	}

	/// <summary>
	/// Journaling is read before any driver exists, so a bundle that could never be journaled is
	/// refused without the database being touched.
	/// </summary>
	[Fact]
	public void EveryMissingJournalingStatementIsListed()
	{
		BundleManifest manifest = Manifest( [Release( "r1" )], [Plan( "a", "r1", "s1" )] );
		InMemoryBundleReader reader = ReaderFor( manifest, _layout );

		reader.Add( "manifest.json", ManifestJson( manifest ) );
		reader.Remove( "journaling/PrepareJournal.sql" );
		reader.Remove( "journaling/OnDeployingStep.sql" );

		BundlingException error = Assert.Throws<BundlingException>(
			() => BundleValidator.Validate( reader, Layouts() ) );

		Assert.Contains( "journaling/PrepareJournal.sql", error.Message );
		Assert.Contains( "journaling/OnDeployingStep.sql", error.Message );
	}

	[Fact]
	public void AQueryStatementBundledWithTwoStatementsIsRefused()
	{
		BundleManifest manifest = Manifest( [Release( "r1" )], [Plan( "a", "r1", "s1" )] );
		InMemoryBundleReader reader = ReaderFor( manifest, _layout );

		reader.Add( "manifest.json", ManifestJson( manifest ) );
		reader.Add( "journaling/GetDeployedPlans.sql", "select 1\n;;\nselect 2" );

		BundlingException error = Assert.Throws<BundlingException>(
			() => BundleValidator.Validate( reader, Layouts() ) );

		Assert.Contains( "GetDeployedPlans is a query", error.Message );
	}

	[Fact]
	public void EveryMissingScriptIsListed()
	{
		BundleManifest manifest = Manifest( [Release( "r1" )], [Plan( "a", "r1", "s1" ), Plan( "b", "r1", "s1" )] );
		InMemoryBundleReader reader = ReaderFor( manifest, _layout );

		reader.Add( "manifest.json", ManifestJson( manifest ) );
		reader.Remove( _layout.ScriptPath( "a", "s1", StepScriptKind.Rollback ) );
		reader.Remove( _layout.ScriptPath( "b", "s1", StepScriptKind.Deploy ) );

		InvalidBundleException error = Assert.Throws<InvalidBundleException>(
			() => BundleValidator.Validate( reader, Layouts() ) );

		Assert.Contains( "plans/a/steps/s1/rollback.sql", error.Message );
		Assert.Contains( "plans/b/steps/s1/deploy.sql", error.Message );
	}

	[Fact]
	public void MissingManifestSurfacesAsAnInvalidManifest()
	{
		Assert.Throws<InvalidBundleManifestException>(
			() => BundleValidator.Validate( new InMemoryBundleReader().Add( BundleMetadata.EntryName, Metadata( _layout ).ToText() ), Layouts() ) );
	}

	[Fact]
	public void MissingMetadataIsRefusedBeforeAnythingElseIsRead()
	{
		BundleManifest manifest = Manifest( [Release( "r1" )], [Plan( "a", "r1", "s1" )] );
		InMemoryBundleReader reader = ReaderFor( manifest, _layout );

		reader.Add( "manifest.json", ManifestJson( manifest ) );
		reader.Remove( BundleMetadata.EntryName );

		InvalidBundleMetadataException error = Assert.Throws<InvalidBundleMetadataException>(
			() => BundleValidator.Validate( reader, Layouts() ) );

		Assert.Contains( BundleMetadata.EntryName, error.Message );
	}

	[Fact]
	public void MalformedMetadataIsRefused()
	{
		BundleManifest manifest = Manifest( [Release( "r1" )], [Plan( "a", "r1", "s1" )] );
		InMemoryBundleReader reader = ReaderFor( manifest, _layout );

		reader.Add( "manifest.json", ManifestJson( manifest ) );
		reader.Add( BundleMetadata.EntryName, "format=1\nnonsense" );

		Assert.Throws<InvalidBundleMetadataException>( () => BundleValidator.Validate( reader, Layouts() ) );
	}

	[Fact]
	public void AnUnknownLayoutIsUnsupportedAndNamesTheKnownOnes()
	{
		BundleManifest manifest = Manifest( [Release( "r1" )], [Plan( "a", "r1", "s1" )] );
		InMemoryBundleReader reader = ReaderFor( manifest, _layout );

		reader.Add( "manifest.json", ManifestJson( manifest ) );
		reader.Add( BundleMetadata.EntryName, Metadata( _layout ).ToText().Replace( "layout=default", "layout=quantum" ) );

		UnsupportedBundleFormatException error = Assert.Throws<UnsupportedBundleFormatException>(
			() => BundleValidator.Validate( reader, Layouts() ) );

		Assert.Equal( "quantum", error.Layout );
		Assert.Equal( 1, error.Format );
		Assert.Null( error.Supported );
		Assert.Contains( "'quantum'", error.Message );
		Assert.Contains( "known: default", error.Message );
		Assert.Contains( "upgrade", error.Message );
	}

	[Fact]
	public void ANewerFormatOfAKnownLayoutIsUnsupported()
	{
		BundleManifest manifest = Manifest( [Release( "r1" )], [Plan( "a", "r1", "s1" )] );
		InMemoryBundleReader reader = ReaderFor( manifest, _layout );

		reader.Add( "manifest.json", ManifestJson( manifest ) );
		reader.Add( BundleMetadata.EntryName, ( Metadata( _layout ) with { Format = _layout.Format + 1 } ).ToText() );

		UnsupportedBundleFormatException error = Assert.Throws<UnsupportedBundleFormatException>(
			() => BundleValidator.Validate( reader, Layouts() ) );

		Assert.Equal( "default", error.Layout );
		Assert.Equal( _layout.Format + 1, error.Format );
		Assert.Equal( _layout.Format, error.Supported );
		Assert.Contains( "upgrade", error.Message );
	}

	/// <summary>
	/// The format gate runs before the manifest is read: a bundle of a newer format may well have a
	/// manifest this reader cannot parse, and must be told apart from a damaged one.
	/// </summary>
	[Fact]
	public void TheFormatIsCheckedBeforeTheManifestIsRead()
	{
		InMemoryBundleReader reader = new ();

		reader.Add( BundleMetadata.EntryName, ( Metadata( _layout ) with { Format = 99 } ).ToText() );
		reader.Add( "manifest.json", "not json at all" );

		Assert.Throws<UnsupportedBundleFormatException>( () => BundleValidator.Validate( reader, Layouts() ) );
	}

	private string ManifestJson( BundleManifest manifest )
	{
		// serialize through the layout by writing a scriptless bundle
		InMemoryBundleWriter writer = new ();

		_layout.Write(
			new Bundle(
				new ProjectDefinition( manifest.Project.Name, manifest.Project.Description, manifest.Project.Engine ),
				manifest.Selection,
				Journaling(),
				manifest.Releases
						.Select( release => new BundleRelease(
									release.Name, release.Description, release.Dependency, release.Finalized ) )
						.ToList(),
				manifest.Plans
						.Select( plan => new BundlePlan(
									plan.Name, plan.Description, plan.Release, plan.Dependencies,
									plan.Steps
										.Select( step => new BundleStep(
													step.Name, step.Description, step.Dependencies,
													() => new StepScripts( null, null, null ) ) )
										.ToList() ) )
						.ToList() ),
			writer );

		return writer.Entries["manifest.json"];
	}

	private sealed class InMemoryBundleWriter : IBundleWriter
	{
		public Dictionary<string, string> Entries { get; } = new ();

		public void Add( string path, Stream content )
		{
			using StreamReader reader = new ( content );

			Entries[path] = reader.ReadToEnd();
		}

		public void Dispose()
		{
		}
	}
}
