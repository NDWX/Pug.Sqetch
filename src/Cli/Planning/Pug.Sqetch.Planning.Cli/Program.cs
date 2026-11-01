using Pug.Sqetch.Planning.Cli;
using Spectre.Console.Cli;

CommandApp app = new ();

app.Configure( SqetchApp.Configure );

return app.Run( args );
