// The Emberward relay. Listens where ASP.NET Core is told to (ASPNETCORE_URLS or --urls), by default
// http://localhost:5000; in production a reverse proxy in front of it terminates TLS.
await Emberward.Relay.RelayApp.Build(args).RunAsync();
