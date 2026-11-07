using RemoteDesktop.Server.Gateway;

var app = GatewayServer.Build(args);
await app.RunAsync().ConfigureAwait(false);
return 0;
