# Switch On Your Code .NET SDK

Official .NET SDK for [Switch On Your Code](https://github.com/switchonyourcode/switchonyourcode).

> **Status:** Early development. The public API is being validated before the first production release.

Switch On Your Code downloads schema-v1 configuration and evaluates flags locally inside your .NET process. Flag evaluation does not make a network request.

## Requirements

- .NET 8 or .NET 10.
- A Switch On Your Code server SDK key (`syoc_server_...`) for the target environment.

## Packages

| Package | Purpose |
| --- | --- |
| `SwitchOnYourCode` | Native dependency-light client, configuration cache and local evaluator. |
| `SwitchOnYourCode.Extensions.DependencyInjection` | ASP.NET Core / Generic Host DI and hosted polling lifecycle. |
| `SwitchOnYourCode.OpenFeature` | OpenFeature provider backed by the same native evaluator. |

The native `SwitchOnYourCode` package does not depend on OpenFeature or `Microsoft.Extensions.*` packages.

## Native SDK

```csharp
using SwitchOnYourCode;

await using var flags = await SwitchOnYourCodeClient.CreateAndWaitAsync(new SwitchOnYourCodeClientOptions
{
    BaseUrl = "https://flags.example.com",
    ServerKey = "syoc_server_...",
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

Typed value and details APIs are available for every Switch On Your Code kind:

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

A caller-provided `HttpClient` can be supplied through `SwitchOnYourCodeClientOptions.HttpClient` for proxy, TLS, tracing, timeout or handler configuration. Caller-provided clients are not disposed by Switch On Your Code.

## ASP.NET Core / Generic Host

Install `SwitchOnYourCode.Extensions.DependencyInjection` and register the SDK once:

```csharp
using SwitchOnYourCode.Extensions.DependencyInjection;

builder.Services.AddSwitchOnYourCode(options =>
{
    options.BaseUrl = "https://flags.example.com";
    options.ServerKey = builder.Configuration["SwitchOnYourCode:ServerKey"]!;
    options.PollInterval = TimeSpan.FromSeconds(30);
});
```

`SwitchOnYourCodeClient` is registered as a singleton. The hosted service performs the initial configuration load during application startup and polls in the background by default. Set `AutoPoll = false` when the host should only perform the initial load.

Inject it normally:

```csharp
app.MapGet("/checkout", (SwitchOnYourCodeClient flags) =>
    flags.GetBooleanValue("new-checkout", false)
        ? Results.Ok("new")
        : Results.Ok("classic"));
```

## Targeting and rollout

The .NET evaluator implements the same schema-v1 contract as Switch On Your Code's Go reference evaluator and the JavaScript/Python SDKs:

- boolean, string, number and JSON flags;
- named variants;
- ordered first-match targeting rules;
- arbitrary nested evaluation context;
- reusable/transitive segments;
- deterministic percentage and multivariate rollouts;
- `targetingKey` or custom scalar rollout bucketing;
- RE2-compatible regular-expression subset;
- Switch On Your Code semantic-version comparisons, including shorthand such as `2.4`;
- deterministic SHA-256 assignment across SDK languages.

The reference compatibility vector is:

```text
environment = env-1
flag        = flag-1
context key = user-123
bucket      = 3837
```

Custom `bucket_by` scalar values are serialized with the Go `encoding/json` representation before hashing. The test suite pins numeric exponent thresholds, HTML-sensitive string escaping and U+2028/U+2029 handling so cohorts stay identical between SDKs.

## OpenFeature

Install `SwitchOnYourCode.OpenFeature` and register the provider:

```csharp
using SwitchOnYourCode;
using SwitchOnYourCode.OpenFeature;
using OpenFeature;

var provider = new SwitchOnYourCodeProvider(new SwitchOnYourCodeProviderOptions
{
    Client = new SwitchOnYourCodeClientOptions
    {
        BaseUrl = "https://flags.example.com",
        ServerKey = "syoc_server_...",
    },
    AutoPoll = true,
});

await Api.Instance.SetProviderAsync(provider);

var client = Api.Instance.GetClient();
var enabled = await client.GetBooleanValueAsync("new-checkout", false);
```

The provider maps Switch On Your Code reasons/errors into OpenFeature resolution details, carries environment/flag/revision/rule metadata, converts OpenFeature evaluation context into the same local evaluator, supports exact integer evaluation without rounding, and emits `PROVIDER_CONFIGURATION_CHANGED` after post-initialization updates.

## Failure behaviour

Typed getters use the caller's fallback when the provider is not ready, the flag does not exist, or the requested type does not match. The corresponding `...Details` method includes the Switch On Your Code reason and error code.

Network errors during later refreshes do not affect local evaluation of the last valid snapshot.

## Development

```bash
dotnet restore tests/SwitchOnYourCode.Tests/SwitchOnYourCode.Tests.csproj
dotnet build tests/SwitchOnYourCode.Tests/SwitchOnYourCode.Tests.csproj -c Release
dotnet run --project tests/SwitchOnYourCode.Tests/SwitchOnYourCode.Tests.csproj -f net8.0 -c Release
dotnet run --project tests/SwitchOnYourCode.Tests/SwitchOnYourCode.Tests.csproj -f net10.0 -c Release
```

CI additionally verifies formatting and packs all three NuGet packages.

## Contributing

Organisation-wide contribution guidelines are maintained in [`switchonyourcode/.github`](https://github.com/switchonyourcode/.github). Switch On Your Code uses a linear Git history and integrates pull requests by rebase only.

## Related repositories

- [Switch On Your Code](https://github.com/switchonyourcode/switchonyourcode)
- [JavaScript / TypeScript SDK](https://github.com/switchonyourcode/sdk-js)
- [Python SDK](https://github.com/switchonyourcode/sdk-python)
- [Go SDK](https://github.com/switchonyourcode/sdk-go)

## Licence

This SDK is licensed under the **Apache License 2.0**. See [`LICENSE`](LICENSE).
