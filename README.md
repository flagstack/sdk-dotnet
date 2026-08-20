# FlagStack .NET SDK

Official .NET SDK for [FlagStack](https://github.com/flagstack/flagstack).

> **Status:** Early development. The public API is being validated before the first production release.

FlagStack downloads schema-v1 configuration and evaluates flags locally inside your .NET process. Flag evaluation does not make a network request.

## Requirements

- .NET 8 or .NET 10.
- A FlagStack server SDK key (`fs_server_...`) for the target environment.

## Packages

| Package | Purpose |
| --- | --- |
| `FlagStack` | Native dependency-light client, configuration cache and local evaluator. |
| `FlagStack.Extensions.DependencyInjection` | ASP.NET Core / Generic Host DI and hosted polling lifecycle. |
| `FlagStack.OpenFeature` | OpenFeature provider backed by the same native evaluator. |

The native `FlagStack` package does not depend on OpenFeature or `Microsoft.Extensions.*` packages.

## Native SDK

```csharp
using FlagStack;

await using var flags = await FlagStackClient.CreateAndWaitAsync(new FlagStackClientOptions
{
    BaseUrl = "https://flags.example.com",
    ServerKey = "fs_server_...",
});

var enabled = flags.GetBooleanValue(
    "new-checkout",
    fallback: false,
    new EvaluationContext(
        TargetingKey: "user-123",
        Attributes: new Dictionary<string, object?>
        {
            ["plan"] = "enterprise",
            ["country"] = "GB",
        }));
```

Typed value and details APIs are available for every FlagStack kind:

```csharp
var enabled = flags.GetBooleanValue("new-checkout", false, context);
var layout = flags.GetStringValue("checkout-layout", "control", context);
var limit = flags.GetNumberValue("max-items", 10, context);
var config = flags.GetJsonValue("checkout-config", new CheckoutConfig(), context);

var details = flags.GetBooleanDetails("new-checkout", false, context);
Console.WriteLine($"{details.Value} {details.Variant} {details.Reason} {details.RuleId}");
```

### Configuration refreshes

`RefreshAsync` uses strong ETag revalidation (`If-None-Match` / `304 Not Modified`). A downloaded document is completely parsed and validated before replacing the current snapshot, so a bad refresh cannot destroy the last known-good configuration.

Long-running services can opt into polling:

```csharp
flags.StartPolling();
// ...
await flags.StopPollingAsync();
```

The default interval is 30 seconds. `PollingError` reports refresh failures without discarding the active snapshot, and `ConfigurationChanged` is emitted only when the validated configuration actually changes.

A caller-provided `HttpClient` can be supplied through `FlagStackClientOptions.HttpClient` for proxy, TLS, tracing, timeout or handler configuration. Caller-provided clients are not disposed by FlagStack.

## ASP.NET Core / Generic Host

Install `FlagStack.Extensions.DependencyInjection` and register the SDK once:

```csharp
using FlagStack.Extensions.DependencyInjection;

builder.Services.AddFlagStack(options =>
{
    options.BaseUrl = "https://flags.example.com";
    options.ServerKey = builder.Configuration["FlagStack:ServerKey"]!;
    options.PollInterval = TimeSpan.FromSeconds(30);
});
```

`FlagStackClient` is registered as a singleton. The hosted service performs the initial configuration load during application startup and polls in the background by default. Set `AutoPoll = false` when the host should only perform the initial load.

Inject it normally:

```csharp
app.MapGet("/checkout", (FlagStackClient flags) =>
    flags.GetBooleanValue("new-checkout", false)
        ? Results.Ok("new")
        : Results.Ok("classic"));
```

## Targeting and rollout

The .NET evaluator implements the same schema-v1 contract as FlagStack's Go reference evaluator and the JavaScript/Python SDKs:

- boolean, string, number and JSON flags;
- named variants;
- ordered first-match targeting rules;
- arbitrary nested evaluation context;
- reusable/transitive segments;
- deterministic percentage and multivariate rollouts;
- `targetingKey` or custom scalar rollout bucketing;
- RE2-compatible regular-expression subset;
- FlagStack semantic-version comparisons, including shorthand such as `2.4`;
- deterministic SHA-256 assignment across SDK languages.

The reference compatibility vector is:

```text
environment = env-1
flag        = flag-1
context key = user-123
bucket      = 22683
```

Custom `bucket_by` scalar values are serialized with the Go `encoding/json` representation before hashing. The test suite pins numeric exponent thresholds, HTML-sensitive string escaping and U+2028/U+2029 handling so cohorts stay identical between SDKs.

## OpenFeature

Install `FlagStack.OpenFeature` and register the provider:

```csharp
using FlagStack;
using FlagStack.OpenFeature;
using OpenFeature;

var provider = new FlagStackProvider(new FlagStackProviderOptions
{
    Client = new FlagStackClientOptions
    {
        BaseUrl = "https://flags.example.com",
        ServerKey = "fs_server_...",
    },
    AutoPoll = true,
});

await Api.Instance.SetProviderAsync(provider);

var client = Api.Instance.GetClient();
var enabled = await client.GetBooleanValueAsync("new-checkout", false);
```

The provider maps FlagStack reasons/errors into OpenFeature resolution details, carries environment/flag/revision/rule metadata, converts OpenFeature evaluation context into the same local evaluator, supports exact integer evaluation without rounding, and emits `PROVIDER_CONFIGURATION_CHANGED` after post-initialization updates.

## Failure behaviour

Typed getters use the caller's fallback when the provider is not ready, the flag does not exist, or the requested type does not match. The corresponding `...Details` method includes the FlagStack reason and error code.

Network errors during later refreshes do not affect local evaluation of the last valid snapshot.

## Development

```bash
dotnet restore tests/FlagStack.Tests/FlagStack.Tests.csproj
dotnet build tests/FlagStack.Tests/FlagStack.Tests.csproj -c Release
dotnet run --project tests/FlagStack.Tests/FlagStack.Tests.csproj -f net8.0 -c Release
dotnet run --project tests/FlagStack.Tests/FlagStack.Tests.csproj -f net10.0 -c Release
```

CI additionally verifies formatting and packs all three NuGet packages.

## Contributing

Organisation-wide contribution guidelines are maintained in [`flagstack/.github`](https://github.com/flagstack/.github). FlagStack uses a linear Git history and integrates pull requests by rebase only.

## Related repositories

- [FlagStack](https://github.com/flagstack/flagstack)
- [JavaScript / TypeScript SDK](https://github.com/flagstack/sdk-js)
- [Python SDK](https://github.com/flagstack/sdk-python)
- [Go SDK](https://github.com/flagstack/sdk-go)

## Licence

This SDK is licensed under the **Apache License 2.0**. See [`LICENSE`](LICENSE).
