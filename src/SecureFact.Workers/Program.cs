namespace SecureFact.Workers;

internal static class WorkerProgram
{
    private static async Task Main(string[] args) => await WorkerHost.Create(args).Build().RunAsync();
}
