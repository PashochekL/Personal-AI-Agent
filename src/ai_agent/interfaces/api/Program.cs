using AiAgent.Api;

try
{
    await using var app = await BackendApplication.BuildAsync(BackendOptions.FromEnvironment());
    await app.RunAsync();
    return 0;
}
catch (Exception e) when (e is not OutOfMemoryException)
{
    Console.Error.WriteLine("Backend could not start or stopped unexpectedly. Check configuration and PostgreSQL/Redis availability; only one Worker per owner/project is allowed.");
    return 1;
}
