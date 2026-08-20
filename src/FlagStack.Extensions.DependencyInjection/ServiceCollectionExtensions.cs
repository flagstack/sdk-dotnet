using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace FlagStack.Extensions.DependencyInjection;

public sealed class FlagStackServiceOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string ServerKey { get; set; } = string.Empty;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);
    public bool AutoPoll { get; set; } = true;
    public HttpClient? HttpClient { get; set; }
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFlagStack(
        this IServiceCollection services,
        Action<FlagStackServiceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new FlagStackServiceOptions();
        configure(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton(provider =>
        {
            var configured = provider.GetRequiredService<FlagStackServiceOptions>();
            return new FlagStackClient(new FlagStackClientOptions
            {
                BaseUrl = configured.BaseUrl,
                ServerKey = configured.ServerKey,
                PollInterval = configured.PollInterval,
                HttpClient = configured.HttpClient,
            });
        });
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, FlagStackHostedService>());
        return services;
    }
}

internal sealed class FlagStackHostedService(
    FlagStackClient client,
    FlagStackServiceOptions options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await client.RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (options.AutoPoll) client.StartPolling();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await client.StopPollingAsync().ConfigureAwait(false);
    }
}
