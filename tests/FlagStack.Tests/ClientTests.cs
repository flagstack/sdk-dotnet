using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace FlagStack.Tests;

public sealed class ClientTests
{
    [Fact]
    public async Task RefreshUsesBearerAndEtagAndEvaluatesLocally()
    {
        var requests = 0;
        using var http = new HttpClient(new HttpTestHandler((request, _) =>
        {
            Interlocked.Increment(ref requests);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("fs_server_test", request.Headers.Authorization?.Parameter);
            if (request.Headers.IfNoneMatch.Any(tag => tag.Tag == "\"v1\""))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
            return Task.FromResult(HttpTestHandler.Json(TestConfiguration.BooleanConfiguration(true), "\"v1\""));
        }));

        await using var client = await FlagStackClient.CreateAndWaitAsync(new FlagStackClientOptions
        {
            BaseUrl = "https://flags.example.com",
            ServerKey = "fs_server_test",
            HttpClient = http,
        });

        Assert.True(client.IsReady);
        Assert.True(client.GetBooleanValue("new-checkout", false));
        var second = await client.RefreshAsync();
        Assert.False(second.Modified);
        Assert.Equal(2, requests);
    }

    [Fact]
    public async Task InvalidRefreshKeepsLastKnownGoodConfiguration()
    {
        var invalid = false;
        using var http = new HttpClient(new HttpTestHandler((_, _) => Task.FromResult(HttpTestHandler.Json(invalid ? "{\"schema_version\":99}" : TestConfiguration.BooleanConfiguration(true)))));
        await using var client = await FlagStackClient.CreateAndWaitAsync(Options(http));
        invalid = true;
        await Assert.ThrowsAsync<FlagStackConfigurationException>(() => client.RefreshAsync());
        Assert.True(client.GetBooleanValue("new-checkout", false));
    }

    [Fact]
    public async Task AuthenticationFailureIsTyped()
    {
        using var http = new HttpClient(new HttpTestHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))));
        await using var client = new FlagStackClient(Options(http));
        await Assert.ThrowsAsync<FlagStackAuthenticationException>(() => client.RefreshAsync());
    }

    [Fact]
    public async Task TypedFallbacksAndRawErrorsAreSafeBeforeReady()
    {
        using var http = new HttpClient(new HttpTestHandler((_, _) => throw new InvalidOperationException()));
        await using var client = new FlagStackClient(Options(http));
        var details = client.GetBooleanDetails("missing", true);
        Assert.True(details.Value);
        Assert.Equal(EvaluationErrorCode.ProviderNotReady, details.ErrorCode);
        var raw = client.GetRawDetails("missing");
        Assert.Equal(EvaluationErrorCode.ProviderNotReady, raw.ErrorCode);
        Assert.Equal(JsonValueKind.Null, raw.Value.ValueKind);
    }

    [Fact]
    public async Task PollingRetainsConfigurationAfterErrors()
    {
        var calls = 0;
        var pollingError = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new HttpTestHandler((_, _) =>
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 1) return Task.FromResult(HttpTestHandler.Json(TestConfiguration.BooleanConfiguration(true)));
            throw new HttpRequestException("offline");
        }));
        await using var client = await FlagStackClient.CreateAndWaitAsync(new FlagStackClientOptions
        {
            BaseUrl = "https://flags.example.com", ServerKey = "fs_server_test", HttpClient = http, PollInterval = TimeSpan.FromMilliseconds(5),
        });
        client.PollingError += exception => pollingError.TrySetResult(exception);
        client.StartPolling();
        var error = await pollingError.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsType<HttpRequestException>(error);
        Assert.True(client.GetBooleanValue("new-checkout", false));
        await client.StopPollingAsync();
    }

    [Fact]
    public async Task ConcurrentRefreshEvaluationAndSubscriptionsRemainConsistent()
    {
        var version = 0;
        using var http = new HttpClient(new HttpTestHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(1, cancellationToken);
            var current = Interlocked.Increment(ref version);
            return HttpTestHandler.Json(TestConfiguration.BooleanConfiguration(current % 2 == 1), $"\"v{current}\"");
        }));
        await using var client = await FlagStackClient.CreateAndWaitAsync(Options(http));
        var snapshots = new ConcurrentBag<FlagStackConfiguration>();
        client.ConfigurationChanged += snapshots.Add;
        var refreshes = Enumerable.Range(0, 20).Select(_ => client.RefreshAsync()).ToArray();
        var evaluations = Enumerable.Range(0, 200).Select(_ => Task.Run(() =>
        {
            _ = client.GetBooleanValue("new-checkout", false, new EvaluationContext("user-123"));
            _ = client.Configuration;
            _ = client.GetFlagInfo("new-checkout");
        })).ToArray();
        await Task.WhenAll(refreshes.Cast<Task>().Concat(evaluations));
        Assert.True(client.IsReady);
        Assert.NotEmpty(snapshots);
    }

    private static FlagStackClientOptions Options(HttpClient http) => new() { BaseUrl = "https://flags.example.com", ServerKey = "fs_server_test", HttpClient = http };
}
