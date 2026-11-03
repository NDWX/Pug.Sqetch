using Pug.Sqetch.Planning.Cli;

using Spectre.Console.Cli;

CommandApp app = new( SqetchApp.CreateDefaultRegistrar() );

app.Configure( SqetchApp.Configure );

return app.Run( args );