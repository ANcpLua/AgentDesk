using Microsoft.Testing.Platform.Builder;

namespace AgentDesk.Tests;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var builder = await TestApplication.CreateBuilderAsync(args);
        builder.AddSelfRegisteredExtensions(args);
        using var app = await builder.BuildAsync();
        return await app.RunAsync();
    }
}
