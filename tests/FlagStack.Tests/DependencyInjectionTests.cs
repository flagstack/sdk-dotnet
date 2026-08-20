using FlagStack.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FlagStack.Tests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public async Task HostedServiceLoadsConfigurationAndRegistersSingleton()
    {
        using var http = new HttpClient(new HttpTestHandler((_, _) =>
            Task.FromResult(HttpTestHandler.Json(TestConfiguration.BooleanConfiguration(true)))));
        var services = new ServiceCollection();
        services.AddFlagStack(options =>
        {
            options.BaseUrl = "https://flags.example.com";
            options.ServerKey = "fs_server_test";
            options.HttpClient = http;
            options.AutoPoll = false;
        });
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<FlagStackClient>();
        Assert.Same(client, provider.GetRequiredService<FlagStackClient>());
        var hosted = provider.GetServices<IHostedService>().Single();

        await hosted.StartAsync(CancellationToken.None);
        Assert.True(client.IsReady);
        Assert.True(client.GetBooleanValue("new-checkout", false));
        await hosted.StopAsync(CancellationToken.None);
    }
}
