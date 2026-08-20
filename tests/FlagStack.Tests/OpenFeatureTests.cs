using FlagStack.OpenFeature;
using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Model;
using OFContext = OpenFeature.Model.EvaluationContext;

namespace FlagStack.Tests;

public sealed class OpenFeatureTests
{
    [Fact]
    public async Task ProviderResolvesNativeValuesAndMetadata()
    {
        using var http = new HttpClient(new HttpTestHandler((_, _) => Task.FromResult(HttpTestHandler.Json(TestConfiguration.OpenFeatureConfiguration()))));
        var provider = Provider(http);
        await provider.InitializeAsync(OFContext.Empty);

        var context = OFContext.Builder().SetTargetingKey("user-123").Set("plan", "enterprise").Build();
        var boolean = await provider.ResolveBooleanValueAsync("new-checkout", false, context);
        Assert.True(boolean.Value);
        Assert.Equal(Reason.Static, boolean.Reason);
        Assert.Equal("on", boolean.Variant);
        Assert.Equal("production", boolean.FlagMetadata?.GetString("flagstack.environment"));
        Assert.Equal("flag-bool", boolean.FlagMetadata?.GetString("flagstack.flag_id"));

        var integer = await provider.ResolveIntegerValueAsync("max-items", 0, context);
        Assert.Equal(10, integer.Value);
        Assert.Equal(ErrorType.None, integer.ErrorType);
        var fractional = await provider.ResolveIntegerValueAsync("ratio", 7, context);
        Assert.Equal(7, fractional.Value);
        Assert.Equal(ErrorType.TypeMismatch, fractional.ErrorType);

        var structured = await provider.ResolveStructureValueAsync("checkout-copy", new Value(), context);
        Assert.Equal(ErrorType.None, structured.ErrorType);
        Assert.True(structured.Value.IsStructure);
        Assert.Equal("Checkout", structured.Value.AsStructure!["title"].AsString);
        await provider.ShutdownAsync();
    }

    [Fact]
    public async Task ProviderWorksThroughOpenFeatureApi()
    {
        using var http = new HttpClient(new HttpTestHandler((_, _) => Task.FromResult(HttpTestHandler.Json(TestConfiguration.OpenFeatureConfiguration()))));
        var provider = Provider(http);
        try
        {
            await Api.Instance.SetProviderAsync(provider);
            var client = Api.Instance.GetClient();
            var context = OFContext.Builder().SetTargetingKey("user-123").Build();
            Assert.True(await client.GetBooleanValueAsync("new-checkout", false, context));
        }
        finally
        {
            await Api.Instance.ShutdownAsync();
        }
    }

    [Fact]
    public async Task ConfigurationChangeIsEmittedOnlyAfterInitialization()
    {
        var revision = 1;
        using var http = new HttpClient(new HttpTestHandler((_, _) => Task.FromResult(HttpTestHandler.Json(TestConfiguration.OpenFeatureConfiguration(revision)))));
        var provider = Provider(http);
        await provider.InitializeAsync(OFContext.Empty);
        Assert.False(provider.GetEventChannel().Reader.TryRead(out _));
        revision = 2;
        await provider.Client.RefreshAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var payload = Assert.IsType<ProviderEventPayload>(await provider.GetEventChannel().Reader.ReadAsync(timeout.Token));
        Assert.Equal(ProviderEventTypes.ProviderConfigurationChanged, payload.Type);
        Assert.Contains("new-checkout", payload.FlagsChanged!);
        Assert.Equal("production", payload.EventMetadata?.GetString("environment"));
        await provider.ShutdownAsync();
    }

    [Fact]
    public async Task OpenFeatureDateContextIsNormalizedDeterministically()
    {
        const string json = """
        {"schema_version":1,"environment":{"id":"env-1","key":"production"},"flags":[{"id":"flag-1","key":"date-match","kind":"boolean","default_value":false,"enabled":true,"variants":[],"policy":{"rules":[{"id":"date","match":"all","conditions":[{"attribute":"created_at","operator":"equals","value":"2026-08-20T03:25:00Z"}],"outcome":{"variant":"on"}}],"fallthrough":{"variant":"off"}},"revision":1}],"segments":[]}
        """;
        using var http = new HttpClient(new HttpTestHandler((_, _) => Task.FromResult(HttpTestHandler.Json(json))));
        var provider = Provider(http);
        await provider.InitializeAsync(OFContext.Empty);
        var context = OFContext.Builder().Set("created_at", new DateTime(2026, 8, 20, 3, 25, 0, DateTimeKind.Utc)).Build();
        var result = await provider.ResolveBooleanValueAsync("date-match", false, context);
        Assert.True(result.Value);
        await provider.ShutdownAsync();
    }

    private static FlagStackProvider Provider(HttpClient http) => new(new FlagStackProviderOptions
    {
        Client = new FlagStackClientOptions { BaseUrl = "https://flags.example.com", ServerKey = "fs_server_test", HttpClient = http },
    });
}
