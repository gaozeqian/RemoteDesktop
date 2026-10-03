// Relay server: pairs peers by relay ticket and forwards their UDP datagrams without decrypting them.
// The forwarding loop is added in milestone M1.
var builder = Host.CreateApplicationBuilder(args);

await builder.Build().RunAsync().ConfigureAwait(false);
