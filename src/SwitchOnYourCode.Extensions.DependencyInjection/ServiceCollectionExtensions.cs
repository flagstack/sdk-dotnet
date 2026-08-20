using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace SwitchOnYourCode.Extensions.DependencyInjection;

public sealed class SwitchOnYourCodeServiceOptions
{
    public string BaseUrl { get; set; } = string.Empty;
    public string ServerKey { get; set; } = string.Empty;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);
    public bool AutoPoll { get; set; } = true;
    public HttpClient? HttpClient { get; set; }
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSwitchOnYourCode(
        this IServiceCollection services,
        Action<SwitchOnYourCodeServiceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SwitchOnYourCodeServiceOptions();
        configure(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton(provider =>
        {
            var configured = provider.GetRequiredService<SwitchOnYourCodeServiceOptions>();
            return new SwitchOnYourCodeClient(new SwitchOnYourCodeClientOptions
            {
                BaseUrl = configured.BaseUrl,
                ServerKey = configured.ServerKey,
                PollInterval = configured.PollInterval,
                HttpClient = configured.HttpClient,
            });
        });
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, SwitchOnYourCodeHostedService>());
        return services;
    }
}

internal sealed class SwitchOnYourCodeHostedService(
    SwitchOnYourCodeClient client,
    SwitchOnYourCodeServiceOptions options) : IHostedService
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
