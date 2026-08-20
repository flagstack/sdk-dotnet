using System.Globalization;
using System.Text.Json;
using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Model;
using OFEvaluationContext = OpenFeature.Model.EvaluationContext;
using OFValue = OpenFeature.Model.Value;

namespace FlagStack.OpenFeature;

public sealed class FlagStackProviderOptions
{
    public required FlagStackClientOptions Client { get; init; }
    public bool AutoPoll { get; init; }
}

public sealed class FlagStackProvider : FeatureProvider
{
    private const string ProviderName = "FlagStack";
    private readonly FlagStackClient _client;
    private readonly bool _autoPoll;
    private bool _initialized;
    private bool _subscribed;

    public FlagStackProvider(FlagStackProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _client = new FlagStackClient(options.Client);
        _autoPoll = options.AutoPoll;
    }

    public FlagStackClient Client => _client;
    public override Metadata GetMetadata() => new(ProviderName);

    public override async Task InitializeAsync(OFEvaluationContext context, CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        Subscribe();
        await _client.RefreshAsync(cancellationToken).ConfigureAwait(false);
        _initialized = true;
        if (_autoPoll) _client.StartPolling();
    }

    public override async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        _initialized = false;
        if (_subscribed)
        {
            _client.ConfigurationChanged -= OnConfigurationChanged;
            _subscribed = false;
        }
        await _client.StopPollingAsync().ConfigureAwait(false);
        await _client.DisposeAsync().ConfigureAwait(false);
    }

    public override Task<ResolutionDetails<bool>> ResolveBooleanValueAsync(string flagKey, bool defaultValue, OFEvaluationContext? context = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ToResolution(flagKey, _client.GetBooleanDetails(flagKey, defaultValue, ToFlagStackContext(context))));
    }

    public override Task<ResolutionDetails<string>> ResolveStringValueAsync(string flagKey, string defaultValue, OFEvaluationContext? context = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ToResolution(flagKey, _client.GetStringDetails(flagKey, defaultValue, ToFlagStackContext(context))));
    }

    public override Task<ResolutionDetails<double>> ResolveDoubleValueAsync(string flagKey, double defaultValue, OFEvaluationContext? context = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ToResolution(flagKey, _client.GetNumberDetails(flagKey, defaultValue, ToFlagStackContext(context))));
    }

    public override Task<ResolutionDetails<int>> ResolveIntegerValueAsync(string flagKey, int defaultValue, OFEvaluationContext? context = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = _client.GetFlagInfo(flagKey);
        if (info is null)
        {
            var missing = _client.GetRawDetails(flagKey, ToFlagStackContext(context));
            return Task.FromResult(ToResolution(flagKey, defaultValue, missing));
        }
        if (info.Kind != "number")
        {
            var mismatch = new EvaluationDetails<int>(defaultValue, "default", EvaluationReason.Error, ErrorCode: EvaluationErrorCode.TypeMismatch, ErrorMessage: "FlagStack flag is not a number.");
            return Task.FromResult(ToResolution(flagKey, mismatch));
        }

        var raw = _client.GetRawDetails(flagKey, ToFlagStackContext(context));
        if (raw.ErrorCode != EvaluationErrorCode.None) return Task.FromResult(ToResolution(flagKey, defaultValue, raw));
        var text = raw.Value.GetRawText();
        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || decimal.Truncate(number) != number || number < int.MinValue || number > int.MaxValue)
        {
            var error = new EvaluationDetails<int>(defaultValue, "default", EvaluationReason.Error, raw.RuleId, EvaluationErrorCode.TypeMismatch, "FlagStack number is not an exact OpenFeature Int32 value.");
            return Task.FromResult(ToResolution(flagKey, error));
        }
        return Task.FromResult(ToResolution(flagKey, new EvaluationDetails<int>((int)number, raw.Variant, raw.Reason, raw.RuleId)));
    }

    public override Task<ResolutionDetails<OFValue>> ResolveStructureValueAsync(string flagKey, OFValue defaultValue, OFEvaluationContext? context = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var raw = _client.GetRawDetails(flagKey, ToFlagStackContext(context));
        var info = _client.GetFlagInfo(flagKey);
        if (raw.ErrorCode != EvaluationErrorCode.None) return Task.FromResult(ToResolution(flagKey, defaultValue, raw));
        if (info?.Kind != "json" || raw.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            var error = new EvaluationDetails<OFValue>(defaultValue, "default", EvaluationReason.Error, raw.RuleId, EvaluationErrorCode.TypeMismatch, "FlagStack value is not an OpenFeature structure or list.");
            return Task.FromResult(ToResolution(flagKey, error));
        }
        return Task.FromResult(ToResolution(flagKey, new EvaluationDetails<OFValue>(ToOpenFeatureValue(raw.Value), raw.Variant, raw.Reason, raw.RuleId)));
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        _client.ConfigurationChanged += OnConfigurationChanged;
        _subscribed = true;
    }

    private void OnConfigurationChanged(FlagStackConfiguration configuration)
    {
        if (!_initialized) return;
        EventChannel.Writer.TryWrite(new ProviderEventPayload
        {
            Type = ProviderEventTypes.ProviderConfigurationChanged,
            ProviderName = ProviderName,
            FlagsChanged = configuration.Flags.Select(flag => flag.Key).ToList(),
            Message = "FlagStack configuration changed.",
            EventMetadata = new ImmutableMetadata(new Dictionary<string, object>
            {
                ["environment"] = configuration.Environment.Key,
                ["environment_id"] = configuration.Environment.Id,
            }),
        });
    }

    private ResolutionDetails<T> ToResolution<T>(string flagKey, EvaluationDetails<T> details) =>
        new(flagKey, details.Value, ToErrorType(details.ErrorCode), ToReason(details.Reason), details.Variant, details.ErrorMessage, MetadataFor(flagKey, details.RuleId));

    private ResolutionDetails<T> ToResolution<T>(string flagKey, T fallback, EvaluationDetails<JsonElement> raw) =>
        ToResolution(flagKey, new EvaluationDetails<T>(fallback, raw.Variant, raw.Reason, raw.RuleId, raw.ErrorCode, raw.ErrorMessage));

    private ImmutableMetadata MetadataFor(string flagKey, string? ruleId)
    {
        var metadata = new Dictionary<string, object>();
        var info = _client.GetFlagInfo(flagKey);
        if (info is not null)
        {
            metadata["flagstack.environment"] = info.Environment.Key;
            metadata["flagstack.environment_id"] = info.Environment.Id;
            metadata["flagstack.flag_id"] = info.Id;
            metadata["flagstack.revision"] = (double)info.Revision;
            metadata["flagstack.enabled"] = info.Enabled;
        }
        if (!string.IsNullOrEmpty(ruleId)) metadata["flagstack.rule_id"] = ruleId;
        return new ImmutableMetadata(metadata);
    }

    private static ErrorType ToErrorType(EvaluationErrorCode code) => code switch
    {
        EvaluationErrorCode.None => ErrorType.None,
        EvaluationErrorCode.ProviderNotReady => ErrorType.ProviderNotReady,
        EvaluationErrorCode.FlagNotFound => ErrorType.FlagNotFound,
        EvaluationErrorCode.ParseError => ErrorType.ParseError,
        EvaluationErrorCode.TypeMismatch => ErrorType.TypeMismatch,
        EvaluationErrorCode.InvalidContext => ErrorType.InvalidContext,
        EvaluationErrorCode.TargetingKeyMissing => ErrorType.TargetingKeyMissing,
        _ => ErrorType.General,
    };

    private static string ToReason(EvaluationReason reason) => reason switch
    {
        EvaluationReason.Static => Reason.Static,
        EvaluationReason.Default => Reason.Default,
        EvaluationReason.TargetingMatch => Reason.TargetingMatch,
        EvaluationReason.Split => Reason.Split,
        EvaluationReason.Disabled => Reason.Disabled,
        EvaluationReason.Error => Reason.Error,
        _ => Reason.Unknown,
    };

    private static global::FlagStack.EvaluationContext ToFlagStackContext(OFEvaluationContext? context)
    {
        if (context is null) return global::FlagStack.EvaluationContext.Empty;
        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in context.AsDictionary())
        {
            if (pair.Key == "targetingKey") continue;
            attributes[pair.Key] = FromOpenFeatureValue(pair.Value);
        }
        return new global::FlagStack.EvaluationContext(context.TargetingKey, attributes);
    }

    private static object? FromOpenFeatureValue(OFValue value)
    {
        if (value.IsNull) return null;
        if (value.IsBoolean) return value.AsBoolean;
        if (value.IsNumber) return value.AsDouble;
        if (value.IsString) return value.AsString;
        if (value.IsDateTime) return FormatDateTime(value.AsDateTime!.Value);
        if (value.IsStructure) return value.AsStructure!.ToDictionary(pair => pair.Key, pair => FromOpenFeatureValue(pair.Value), StringComparer.Ordinal);
        if (value.IsList) return value.AsList!.Select(FromOpenFeatureValue).ToList();
        return null;
    }

    private static string FormatDateTime(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);
    }

    private static OFValue ToOpenFeatureValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => new OFValue(new Structure(element.EnumerateObject().ToDictionary(property => property.Name, property => ToOpenFeatureValue(property.Value), StringComparer.Ordinal))),
        JsonValueKind.Array => new OFValue(element.EnumerateArray().Select(ToOpenFeatureValue).ToList()),
        JsonValueKind.String => new OFValue(element.GetString()!),
        JsonValueKind.Number => new OFValue(element.GetDouble()),
        JsonValueKind.True => new OFValue(true),
        JsonValueKind.False => new OFValue(false),
        JsonValueKind.Null => new OFValue(),
        _ => new OFValue(),
    };
}
