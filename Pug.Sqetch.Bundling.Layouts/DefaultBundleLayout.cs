using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pug.Sqetch.Models;

namespace Pug.Sqetch.Bundling.Layouts;

/// <summary>
/// Default bundle layout: 'META-DATA' first (written ahead of the layout by
/// <c>BundlePackager</c>, so a streaming consumer learns the layout and its format before anything
/// else), then 'manifest.json' — so streaming consumers see it before the payload — then the
/// project's journaling SQL as 'journaling/&lt;slot&gt;.sql', then
/// 'plans/&lt;plan&gt;/steps/&lt;step&gt;/{deploy,verify,rollback}.sql'.
/// Journaling precedes the plans because a deployment needs it before it may run any script.
/// Scripts live under 'plans/' so a plan named like the manifest file cannot collide with
/// it. The order of the manifest arrays is the deployment order and part of the contract.
/// </summary>
public sealed class DefaultBundleLayout : IBundleLayout
{
	public const string LayoutName = "default";

	/// <summary>The only format of this layout so far; see <see cref="IBundleLayout.Format"/>.</summary>
	public const int CurrentFormat = 1;

	public const string ManifestEntry = "manifest.json";

	private static readonly JsonSerializerOptions JsonOptions = new ()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
		Converters = { new JsonStringEnumConverter( JsonNamingPolicy.CamelCase ) }
	};

	public string Name => LayoutName;

	public int Format => CurrentFormat;

	public void Write( Bundle bundle, IBundleWriter writer )
	{
		using( MemoryStream manifest = new ( JsonSerializer.SerializeToUtf8Bytes( Manifest( bundle ), JsonOptions ) ) )
			writer.Add( ManifestEntry, manifest );

		foreach( JournalingSlot slot in JournalingSlots.All )
			using( MemoryStream statement = new ( Encoding.UTF8.GetBytes( bundle.Journaling.Text( slot ) ) ) )
				writer.Add( JournalingPath( slot ), statement );

		foreach( BundlePlan plan in bundle.Plans )
			foreach( BundleStep step in plan.Steps )
				WriteScripts( plan, step, writer );
	}

	public BundleManifest ReadManifest( IBundleReader reader )
	{
		if( !reader.Contains( ManifestEntry ) )
			throw new InvalidBundleManifestException( $"Bundle has no '{ManifestEntry}'." );

		BundleManifest? manifest;

		try
		{
			using Stream entry = reader.Open( ManifestEntry );

			manifest = JsonSerializer.Deserialize<BundleManifest>( entry, JsonOptions );
		}
		catch( Exception exception ) when( exception is JsonException or NotSupportedException )
		{
			throw new InvalidBundleManifestException( $"Bundle manifest is invalid: {exception.Message}" );
		}

		if( manifest is null )
			throw new InvalidBundleManifestException( "Bundle manifest is empty." );

		return Normalize( manifest );
	}

	public JournalingStatements ReadJournalingStatements( IBundleReader reader )
	{
		Dictionary<JournalingSlot, string?> text = new ();
		List<string> missing = new ();

		foreach( JournalingSlot slot in JournalingSlots.All )
		{
			string path = JournalingPath( slot );

			if( !reader.Contains( path ) )
			{
				missing.Add( path );

				continue;
			}

			using Stream entry = reader.Open( path );

			// StreamReader strips a byte-order mark; one left in place would be sent to the
			// server as part of the slot's first statement
			using StreamReader content = new ( entry );

			text[slot] = content.ReadToEnd();
		}

		if( missing.Count > 0 )
			throw new BundlingException(
				$"Bundle is missing journaling statements: {string.Join( ", ", missing )}." );

		try
		{
			return new JournalingStatements( text );
		}
		catch( ArgumentException exception )
		{
			// present but blank, or a query slot holding more than one statement
			throw new BundlingException( $"Bundle has invalid journaling statements: {exception.Message}" );
		}
	}

	public string ScriptPath( string plan, string step, StepScriptKind kind )
		=> $"plans/{plan}/steps/{step}/{kind.ToString().ToLowerInvariant()}.sql";

	/// <summary>
	/// Entry path of a journaling slot. Private on purpose: consumers go through
	/// <see cref="ReadJournalingStatements"/>, so nothing outside this layout depends on where the
	/// statements sit. The file name comes from <see cref="JournalingSlots.FileName"/>, the same
	/// helper the project info store uses, so the two can never drift apart.
	/// </summary>
	private static string JournalingPath( JournalingSlot slot ) => $"journaling/{JournalingSlots.FileName( slot )}";

	private static BundleManifest Manifest( Bundle bundle )
		=> new (
			new BundleManifestProject(
				bundle.Project.Name, bundle.Project.Description, bundle.Project.Engine ),
			bundle.Selection,
			bundle.Releases
				.Select( release => new BundleManifestRelease(
							release.Name, release.Description, release.Dependency, release.Finalized ) )
				.ToList(),
			bundle.Plans
				.Select( plan => new BundleManifestPlan(
							plan.Name,
							plan.Description,
							plan.Release,
							plan.Dependencies,
							plan.Steps
								.Select( step => new BundleManifestStep(
											step.Name, step.Description, step.Dependencies ) )
								.ToList() ) )
				.ToList() );

	/// <summary>
	/// Deserialization leaves absent members null despite the record signatures; required
	/// names fail validation, everything else falls back to an empty value.
	/// </summary>
	private static BundleManifest Normalize( BundleManifest manifest )
	{
		if( string.IsNullOrEmpty( manifest.Project?.Name ) )
			throw new InvalidBundleManifestException( "Bundle manifest does not name the project." );

		if( manifest.Plans is null )
			throw new InvalidBundleManifestException( "Bundle manifest has no plan list." );

		IReadOnlyList<BundleManifestRelease> releases = manifest.Releases ?? [];

		return manifest with
		{
			Project = manifest.Project with
			{
				Description = manifest.Project.Description ?? "",
				Engine = manifest.Project.Engine ?? ""
			},
			// manifest array order is the deployment-order contract, so an omitted
			// 'dependency' falls back to the preceding release's (already-validated) name
			Releases = releases
						.Select( ( release, index ) => NormalizeRelease(
									release, index == 0 ? "" : releases[index - 1].Name! ) )
						.ToList(),
			Plans = manifest.Plans
						.Select( NormalizePlan )
						.ToList()
		};
	}

	private static BundleManifestRelease NormalizeRelease( BundleManifestRelease release, string previousName )
	{
		if( string.IsNullOrEmpty( release.Name ) )
			throw new InvalidBundleManifestException( "Bundle manifest contains an unnamed release." );

		return release with
		{
			Description = release.Description ?? "",
			Dependency = release.Dependency ?? previousName
		};
	}

	private static BundleManifestPlan NormalizePlan( BundleManifestPlan plan )
	{
		if( string.IsNullOrEmpty( plan.Name ) )
			throw new InvalidBundleManifestException( "Bundle manifest contains an unnamed plan." );

		return plan with
		{
			Description = plan.Description ?? "",
			Release = plan.Release ?? "",
			Dependencies = plan.Dependencies ?? [],
			Steps = ( plan.Steps ?? [] )
					.Select( step => NormalizeStep( plan.Name, step ) )
					.ToList()
		};
	}

	private static BundleManifestStep NormalizeStep( string plan, BundleManifestStep step )
	{
		if( string.IsNullOrEmpty( step.Name ) )
			throw new InvalidBundleManifestException(
				$"Bundle manifest contains an unnamed step in plan '{plan}'." );

		return step with
		{
			Description = step.Description ?? "",
			Dependencies = step.Dependencies ?? []
		};
	}

	private void WriteScripts( BundlePlan plan, BundleStep step, IBundleWriter writer )
	{
		using StepScripts scripts = step.OpenScripts();

		AddScript( writer, ScriptPath( plan.Name, step.Name, StepScriptKind.Deploy ), scripts.DeployScript );
		AddScript( writer, ScriptPath( plan.Name, step.Name, StepScriptKind.Verify ), scripts.VerifyScript );
		AddScript( writer, ScriptPath( plan.Name, step.Name, StepScriptKind.Rollback ), scripts.RollbackScript );
	}

	private static void AddScript( IBundleWriter writer, string path, Stream? content )
	{
		if( content is not null )
			writer.Add( path, content );
	}
}
