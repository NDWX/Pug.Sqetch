using Pug.Sqetch.Deployment.Cli;
using Spectre.Console.Cli;

CommandApp app = new ( SqetchDeployApp.CreateDefaultRegistrar() );

app.Configure( SqetchDeployApp.Configure );

return app.Run( args );
