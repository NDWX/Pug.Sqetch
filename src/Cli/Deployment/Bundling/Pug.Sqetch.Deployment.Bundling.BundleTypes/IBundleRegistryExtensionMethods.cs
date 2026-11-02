namespace Pug.Sqetch.Deployment.Bundling.BundleTypes;

public static class IBundleRegistryExtensionMethods
{
	public static void RegisterBundleTypes( this IBundleTypeRegistry registry )
	{
		registry.Register( ZipBundleType.TypeName, () => new ZipBundleType() );
		registry.Register( TarBundleType.TypeName, () => new TarBundleType() );
		registry.Register( TarGzBundleType.TypeName, () => new TarGzBundleType() );
		registry.Register( DirectoryBundleType.TypeName, () => new DirectoryBundleType() );
	}
}