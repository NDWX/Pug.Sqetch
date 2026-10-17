using Pug.Sqetch.Bundling;
using Pug.Sqetch.Deployment;
using Pug.Sqetch.DatabaseDriver;
using Pug.Sqetch.Models;
using Pug.Sqetch.ProjectInfoStores.FileSystem;
using Spectre.Console.Cli;

namespace Pug.Sqetch.Cli.Commands.Deployment;

/// <summary>
/// What 'sqetch-deploy' returns to its caller. Codes are grouped by cause: the tens digit names the
/// category and the units digit the specific failure within it, so a pipeline can branch on a whole
/// decade without knowing every member of it. A units digit of 0 is that category's unspecified
/// code — reserved, so adding a specific code later never renumbers an existing one.
///
/// A one-digit code means unclassified. <see cref="Unclassified"/> is what every failure returned
/// before the categories existed and is still what an exception no arm recognizes returns, so a
/// pipeline testing for non-zero is unaffected by any of this.
///
/// Public, unlike the message formatting beside it: what a pipeline reads off a run is part of
/// this CLI's contract, and the tests that pin each code say so by naming them.
/// </summary>
public static class DeployExitCodes
{
	/// <summary>
	/// Deployed, or had nothing to deploy, or deployed and undid it on request. All three are
	/// successes: anything non-zero stops a shell running under 'set -e', and a pipeline that
	/// needs to tell them apart should read the output rather than the exit code.
	/// </summary>
	public const int Success = 0;

	/// <summary>No arm recognized the failure, so the message is all there is to go on.</summary>
	public const int Unclassified = 1;

	/// <summary>
	/// The command line did not validate: unknown command, missing or unmatched argument, unknown
	/// option, bad option value, a parameter the selected driver does not define. Deliberately one
	/// code and not one per kind of mistake — telling them apart means either parsing Spectre's
	/// message text or re-implementing its parser, and nothing has needed the distinction yet.
	/// </summary>
	public const int UsageError = 10;

	public const int BundleError = 20;
	public const int BundleNotFound = 21;
	public const int InvalidBundle = 22;
	public const int IncompatibleBundle = 23;

	/// <summary>
	/// The bundle needs a newer sqetch-deploy: it names a layout this one does not know, or a newer
	/// format of one it does. A pipeline's remedy is upgrading the tool, not fixing the bundle.
	/// </summary>
	public const int UnsupportedBundle = 24;

	public const int DriverError = 30;
	public const int UnknownDriver = 31;
	public const int DriverCreationFailed = 32;

	/// <summary>The driver was configured, but the database could not be reached.</summary>
	public const int DatabaseUnreachable = 33;

	public const int JournalError = 40;
	public const int JournalPreparationFailed = 41;
	public const int JournalQueryFailed = 42;
	public const int JournalWriteFailed = 43;

	public const int DeploymentError = 50;

	/// <summary>A step script failed and the database is as it was.</summary>
	public const int DeploymentFailed = 51;

	/// <summary>The deployment failed partway and what committed is still in place.</summary>
	public const int DeploymentFailedPartway = 52;

	public const int RollbackError = 60;

	/// <summary>Compensation itself failed, so the database is neither deployed nor undone.</summary>
	public const int RollbackFailed = 61;

	public const int BundleBuildError = 70;

	/// <summary>The project could not be opened: not a project directory, damaged, or written by a newer Sqetch.</summary>
	public const int ProjectUnreadable = 71;

	/// <summary>
	/// The project was read but cannot be bundled as it stands: journaling statements unset, a
	/// step script missing, '--since' naming a release the chain lacks, or nothing to bundle.
	/// </summary>
	public const int ProjectNotBundleable = 72;

	/// <summary>The bundle was assembled but its output could not be written: the target exists, or the write failed.</summary>
	public const int BundleOutputFailed = 73;

	public const int NotSupported = 91;

	/// <summary>
	/// The code <paramref name="exception"/> earns. <paramref name="databaseChanged"/> says whether
	/// this run committed anything a rollback did not take back — only the deploy command knows
	/// that, so everywhere else leaves it false, which is correct for a failure raised before the
	/// deployment began.
	/// </summary>
	public static int For( Exception exception, bool databaseChanged = false )
		=> exception switch
		{
			// usage comes first: every arm below it describes something that was read or run
			CommandAppException or CommandLineException => UsageError,

			// the only bundle type name in this CLI is the one --bundle-type carries
			UnknownBundleTypeException => UsageError,

			OperationNotSupportedException => NotSupported,

			// a database left half-compensated outranks the failure that started the rollback
			RollbackFailedException => RollbackFailed,

			// ...and a database left partway outranks the cause of it. The cause is in the message
			// for whoever has to fix it; the code is for a pipeline deciding whether it may retry.
			_ when databaseChanged => DeploymentFailedPartway,

			// bundling failures sit above the bundle-reading arms below: EmptyBundleException is a
			// BundlingException, and as such would otherwise earn InvalidBundle
			ProjectStoreException => ProjectUnreadable,
			MissingJournalingStatementsException or MissingStepScriptsException or UnknownReleaseException
				or EmptyBundleException => ProjectNotBundleable,
			BundleOutputException => BundleOutputFailed,

			MissingBundleException => BundleNotFound,
			IncompatibleBundleException => IncompatibleBundle,

			// before the general BundlingException arm below, which would call it a damaged bundle
			UnsupportedBundleFormatException => UnsupportedBundle,

			// only 'bundle' asks the registry for a layout by name, and the name is this host's own
			// constant there, so an unknown one is a composition mistake rather than anything wrong
			// with the bundle; a layout name read out of a bundle never gets here, it is unsupported
			UnknownBundleLayoutException => Unclassified,
			InvalidBundleException or BundlingException => InvalidBundle,

			UnknownDatabaseDriverException => UnknownDriver,
			DriverCreationException => DriverCreationFailed,
			DatabaseConnectionException => DatabaseUnreachable,

			JournalingStatementException journaling => ForJournalingSlot( journaling.Slot ),
			StepScriptFailedException => DeploymentFailed,

			_ => Unclassified
		};

	/// <summary>
	/// Which journal code a journaling failure earns — the three have different remedies: fix the
	/// provisioning DDL, fix a query or the rows it read, fix a write statement. The exception
	/// carries its slot as text, since <c>Pug.Sqetch.Deployment.Abstractions</c> has no project
	/// references and so cannot name <see cref="JournalingSlot"/>; a slot that will not parse gets
	/// the category's unspecified code rather than a guess.
	/// </summary>
	private static int ForJournalingSlot( string slot )
	{
		if( !JournalingSlots.TryParse( slot, out JournalingSlot parsed ) )
			return JournalError;

		if( parsed == JournalingSlot.PrepareJournal )
			return JournalPreparationFailed;

		return JournalingSlots.IsQuery( parsed ) ? JournalQueryFailed : JournalWriteFailed;
	}
}
