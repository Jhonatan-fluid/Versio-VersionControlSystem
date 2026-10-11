// The regex timeout first, before anything can build a pattern (WebHost's remarks say why).
Versio.Web.WebHost.ConfigureProcess();
Versio.Web.WebHost.Build(args).Run();
