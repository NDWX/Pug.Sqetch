namespace Pug.Sqetch.DatabaseDriver;

/// <summary>
/// Entry point for each database driver
/// </summary>
public interface IDatabaseDriverFactory
{
    /// <summary>
    /// Name of the database driver, will also be used as CLI switch prefix "--[database driver name]-[parameter name]".
    /// Cannot contain spaces or special characters.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Implementation must return a definition of the parameters required by the database driver
    /// </summary>
    /// <returns>Definition of parameters supported and required by the database driver</returns>
    DatabaseDriverParametersDefinition GetParametersDefinition();

    /// <summary>
    /// Implementation will validate provided parameters and create return an instance of IDatabaseDriver that will be used by deployment and change journaling logic
    /// </summary>
    /// <param name="parameters">Parameters provided by user</param>
    /// <param name="stepScriptTimeout">Timeout the driver must apply to every step script execution; <see cref="Timeout.InfiniteTimeSpan"/> means no timeout</param>
    IDatabaseDriver Create(IDictionary<string, string> parameters, TimeSpan stepScriptTimeout);
}