namespace Pug.Sqetch.Cli.Commands.Deployment;

/// <summary>
/// Who is building a bundle, as recorded in its metadata. Injected rather than read from an assembly
/// in here, because the version that matters is the host's: the commands project is not what a
/// pipeline installs, and the host is what <c>--version</c> reports.
/// </summary>
public sealed record BundlerIdentity( string Name, string Version );
