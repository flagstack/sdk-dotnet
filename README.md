# FlagStack .NET SDK

Official .NET SDK for [FlagStack](https://github.com/flagstack/flagstack).

> **Status:** Planned / early development. Not yet ready for production use.

## Goals

The .NET SDK will provide idiomatic FlagStack integration for modern .NET applications, including:

- .NET server applications;
- ASP.NET Core;
- dependency-injection integration;
- asynchronous configuration updates;
- local feature-flag evaluation;
- resilient cached configuration;
- strongly typed APIs;
- OpenFeature integration.

## Packages

The exact NuGet package structure is still to be designed. It may eventually include packages for the core SDK, ASP.NET Core integration and OpenFeature support.

## Design principles

Feature evaluations should normally happen locally inside the application process rather than requiring a network request for every evaluation.

The SDK should remain lightweight, safe for long-running server applications and straightforward to integrate with standard .NET dependency injection.

## Related repositories

- [FlagStack](https://github.com/flagstack/flagstack)
- [Python SDK](https://github.com/flagstack/sdk-python)
- [JavaScript / TypeScript SDK](https://github.com/flagstack/sdk-js)
- [Go SDK](https://github.com/flagstack/sdk-go)

## License

A license will be selected before the first public release.
