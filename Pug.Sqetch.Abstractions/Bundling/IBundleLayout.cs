using Pug.Sqetch.Models;

namespace Pug.Sqetch.Bundling;

/// <summary>
/// Maps assembled bundle content onto bundle entries (manifest and script files). Custom
/// layouts plug in through <see cref="BundleLayoutRegistry"/>. A layout never writes the bundle's
/// <see cref="BundleMetadata.EntryName"/> entry: the metadata names the layout, so it is written
/// before the layout is asked to write anything.
/// </summary>
public interface IBundleLayout
{
	string Name { get; }

	/// <summary>
	/// The format version of this layout: what <see cref="Write"/> produces and the highest a reader
	/// of this layout understands. A bundle declaring a higher one is refused rather than misread.
	/// </summary>
	int Format { get; }

	void Write( Bundle bundle, IBundleWriter writer );

	/// <summary>
	/// Reads and validates the manifest of a bundle; throws
	/// <see cref="InvalidBundleManifestException"/> when it is missing or invalid.
	/// </summary>
	BundleManifest ReadManifest( IBundleReader reader );

	/// <summary>
	/// Reads the project's journaling SQL back out of a bundle; throws
	/// <see cref="BundlingException"/> when any slot is missing or unreadable.
	/// Where the statements live is this layout's business — a layout is free to keep them in
	/// separate entries or inside its manifest — so consumers read them through here rather than
	/// composing entry paths of their own.
	/// </summary>
	JournalingStatements ReadJournalingStatements( IBundleReader reader );

	/// <summary>Entry path of a step's script within this layout.</summary>
	string ScriptPath( string plan, string step, StepScriptKind kind );
}
