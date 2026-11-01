namespace Pug.Sqetch.DatabaseDriver;

/// <summary>
/// Definition of parameter used by a database driver
/// </summary>
/// <param name="Name">Name of parameter, also to be used in "--[database-driver-name]-[parameter-name]" command line switch; Cannot contain spaces or special characters.</param>
/// <param name="Description">Description of parameter</param>
public record DatabaseDriverParameterDefinition(string Name, string Description);