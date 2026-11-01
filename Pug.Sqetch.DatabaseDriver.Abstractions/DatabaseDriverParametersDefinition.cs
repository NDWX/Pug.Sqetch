namespace Pug.Sqetch.DatabaseDriver;

/// <summary>
/// Represents the definition of parameters required and supported by a database driver.
/// This includes both the list of all available parameters and the possible
/// combinations of parameters that are required for successful operation.
/// </summary>
/// <param name="Parameters">
/// A collection of <see cref="DatabaseDriverParameterDefinition"/> List of all parameters
/// supported by the database driver, including their names and descriptions.
/// </param>
/// <param name="RequiredParametersOptions">
/// List of minimum required parameter grouping options. At least one grouping must be provided for successful creation of a database driver instance.
/// </param>
public record DatabaseDriverParametersDefinition(
    IEnumerable<DatabaseDriverParameterDefinition> Parameters,
    ICollection<ICollection<string>> RequiredParametersOptions);