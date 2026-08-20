using System.Text;

namespace FlagStack.Tests;

internal static class TestConfiguration
{
    internal static FlagStackConfiguration Parse(string json) =>
        ConfigurationParser.Parse(Encoding.UTF8.GetBytes(json));

    internal static string BooleanConfiguration(bool enabled = true, long revision = 1) => $$"""
    {
      "schema_version": 1,
      "environment": {"id":"env-1","key":"production"},
      "flags": [{
        "id":"flag-1","key":"new-checkout","kind":"boolean","default_value":false,
        "enabled":{{enabled.ToString().ToLowerInvariant()}},"variants":[],"policy":{},"revision":{{revision}}
      }],
      "segments": []
    }
    """;

    internal static string OpenFeatureConfiguration(int revision = 1) => $$"""
    {
      "schema_version": 1,
      "environment": {"id":"env-1","key":"production"},
      "flags": [
        {"id":"flag-bool","key":"new-checkout","kind":"boolean","default_value":false,"enabled":true,"variants":[],"policy":{},"revision":{{revision}}},
        {"id":"flag-number","key":"max-items","kind":"number","default_value":10,"enabled":true,"variants":[],"policy":{"fallthrough":{"variant":"default"}},"revision":1},
        {"id":"flag-fraction","key":"ratio","kind":"number","default_value":1.5,"enabled":true,"variants":[],"policy":{"fallthrough":{"variant":"default"}},"revision":1},
        {"id":"flag-json","key":"checkout-copy","kind":"json","default_value":{"title":"Checkout","steps":[1,2]},"enabled":true,"variants":[],"policy":{"fallthrough":{"variant":"default"}},"revision":1}
      ],
      "segments": []
    }
    """;
}
