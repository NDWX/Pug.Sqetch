using Pug.Sqetch.Deployment.Bundling;
using Pug.Sqetch.Deployment.Bundling.BundleTypes;
using Pug.Sqetch.DatabaseDriver;

using Spectre.Console.Cli;

namespace Pug.Sqetch.Deployment.Cli.Commands;

/// <summary>
/// What every command acting on a bundle and a database does before its own work: work out the
/// bundle's type and create the driver from the command line's driver switches. Shared so 'deploy'
/// and 'status' cannot disagree about either.
/// </summary>
internal static class DeploymentTarget
{
	/// <summary>
	/// The type named by '--bundle-type', or else the one the path implies: a directory, a
	/// '.tar.gz'/'.tgz' or '.tar' file, and zip for anything else.
	/// </summary>
	public static IBundleType ResolveBundleType( IBundleTypeRegistry bundleTypes, string bundle, string? bundleType )
	{
		if( bundleType is not null )
			return bundleTypes.Create( bundleType );

		if( Directory.Exists( bundle ) )
			return bundleTypes.Create( DirectoryBundleType.TypeName );

		if( bundle.EndsWith( ".tar.gz", StringComparison.OrdinalIgnoreCase ) ||
			bundle.EndsWith( ".tgz", StringComparison.OrdinalIgnoreCase ) )
			return bundleTypes.Create( TarGzBundleType.TypeName );

		if( bundle.EndsWith( ".tar", StringComparison.OrdinalIgnoreCase ) )
			return bundleTypes.Create( TarBundleType.TypeName );

		return bundleTypes.Create( ZipBundleType.TypeName );
	}

	/// <summary>
	/// Creates the named driver from the '--&lt;driver&gt;-&lt;parameter&gt;' switches left over
	/// after parsing. A driver that refuses its parameters is <see cref="DriverCreationException"/>;
	/// it does not connect yet, so an unreachable database surfaces at the first transaction.
	/// </summary>
	public static IDatabaseDriver CreateDriver(
		IDatabaseDriverRegistry drivers,
		string driver,
		IRemainingArguments remaining,
		TimeSpan stepScriptTimeout
	)
	{
		IDatabaseDriverFactory factory = drivers.Create( driver );

		IDictionary<string, string> parameters = DriverParameters.Parse( remaining, factory );

		try
		{
			return factory.Create( parameters, stepScriptTimeout );
		}
		catch( Exception exception )
		{
			throw new DriverCreationException( factory.Name, exception );
		}
	}
}
