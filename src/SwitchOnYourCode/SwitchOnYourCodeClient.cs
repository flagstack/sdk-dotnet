using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SwitchOnYourCode;

public sealed class SwitchOnYourCodeClientOptions
{
    public required string BaseUrl { get; init; }
    public required string ServerKey { get; init; }
    public HttpClient? HttpClient { get; init; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed class SwitchOnYourCodeClient : IAsyncDisposable, IDisposable
{
    private const int MaxConfigurationBytes = 10 * 1024 * 1024;
    private readonly Uri _baseUri;
    private readonly string _serverKey;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly TimeSpan _pollInterval;
    private readonly object _stateLock = new();
    private SwitchOnYourCodeConfiguration? _configuration;
    private Dictionary<string, FeatureFlag> _flags = new(StringComparer.Ordinal);
    private string? _etag;
    private CancellationTokenSource? _pollCancellation;
    private Task? _pollTask;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private bool _disposed;

    public SwitchOnYourCodeClient(SwitchOnYourCodeClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var baseUrl = options.BaseUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("SwitchOnYourCode BaseUrl must be an absolute http(s) URL.", nameof(options));
        }
        _baseUri = baseUri;

        _serverKey = options.ServerKey?.Trim() ?? string.Empty;
        if (!_serverKey.StartsWith("syoc_server_", StringComparison.Ordinal))
            throw new ArgumentException(".NET SDK requires a SwitchOnYourCode server key (syoc_server_...).", nameof(options));
        if (options.PollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "PollInterval must be positive.");

        _pollInterval = options.PollInterval;
        if (options.HttpClient is null)
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = options.HttpClient;
        }
    }

    public event Action<SwitchOnYourCodeConfiguration>? ConfigurationChanged;
    public event Action<Exception>? PollingError;

    public bool IsReady
    {
        get { lock (_stateLock) return _configuration is not null; }
    }

    public string? ETag
    {
        get { lock (_stateLock) return _etag; }
    }

    public SwitchOnYourCodeConfiguration? Configuration
    {
        get
        {
            lock (_stateLock)
                return _configuration is null ? null : ConfigurationParser.Clone(_configuration);
        }
    }

    public static async Task<SwitchOnYourCodeClient> CreateAndWaitAsync(SwitchOnYourCodeClientOptions options, bool startPolling = false, CancellationToken cancellationToken = default)
    {
        var client = new SwitchOnYourCodeClient(options);
        try
        {
            await client.RefreshAsync(cancellationToken).ConfigureAwait(false);
            if (startPolling) client.StartPolling(cancellationToken);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<RefreshResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri.AbsoluteUri.TrimEnd('/') + "/sdk/v1/config", UriKind.Absolute));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _serverKey);
            lock (_stateLock)
            {
                if (_etag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", _etag);
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                var existing = Configuration;
                if (existing is null) throw new SwitchOnYourCodeConfigurationException("SwitchOnYourCode returned 304 before any configuration was loaded.");
                return new RefreshResult(false, existing);
            }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new SwitchOnYourCodeAuthenticationException($"SwitchOnYourCode SDK authentication failed with HTTP {(int)response.StatusCode}.");
            if (!response.IsSuccessStatusCode)
                throw new SwitchOnYourCodeHttpException((int)response.StatusCode, $"SwitchOnYourCode configuration request failed with HTTP {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentLength is > MaxConfigurationBytes)
                throw new SwitchOnYourCodeConfigurationException("SwitchOnYourCode configuration response exceeds 10 MiB.");

            var payload = await ReadLimitedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            var configuration = ConfigurationParser.Parse(payload);
            var responseEtag = response.Headers.ETag?.ToString();

            bool modified;
            lock (_stateLock)
            {
                modified = _configuration is null || !ConfigurationsEqual(_configuration, configuration);
                _configuration = ConfigurationParser.Clone(configuration);
                _flags = _configuration.Flags.ToDictionary(flag => flag.Key, StringComparer.Ordinal);
                _etag = responseEtag;
            }

            if (modified)
            {
                var snapshot = ConfigurationParser.Clone(configuration);
                ConfigurationChanged?.Invoke(snapshot);
            }
            return new RefreshResult(modified, ConfigurationParser.Clone(configuration));
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public void StartPolling(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_stateLock)
        {
            if (_pollTask is { IsCompleted: false }) throw new InvalidOperationException("SwitchOnYourCode polling is already running.");
            _pollCancellation?.Dispose();
            _pollCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _pollTask = PollAsync(_pollCancellation.Token);
        }
    }

    public async Task StopPollingAsync()
    {
        CancellationTokenSource? cancellation;
        Task? task;
        lock (_stateLock)
        {
            cancellation = _pollCancellation;
            task = _pollTask;
            _pollCancellation = null;
            _pollTask = null;
        }
        if (cancellation is null) return;
        await cancellation.CancelAsync().ConfigureAwait(false);
        try { if (task is not null) await task.ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        cancellation.Dispose();
    }

    public FeatureFlagInfo? GetFlagInfo(string flagKey)
    {
        lock (_stateLock)
        {
            if (_configuration is null || !_flags.TryGetValue(flagKey, out var flag)) return null;
            return new FeatureFlagInfo(flag.Id, flag.Key, flag.Kind, flag.Enabled, flag.Revision,
                new SwitchOnYourCodeEnvironment { Id = _configuration.Environment.Id, Key = _configuration.Environment.Key });
        }
    }

    public bool GetBooleanValue(string flagKey, bool fallback, EvaluationContext? context = null) => GetBooleanDetails(flagKey, fallback, context).Value;
    public EvaluationDetails<bool> GetBooleanDetails(string flagKey, bool fallback, EvaluationContext? context = null) => EvaluateTyped(flagKey, "boolean", fallback, context, element => element.GetBoolean());
    public string GetStringValue(string flagKey, string fallback, EvaluationContext? context = null) => GetStringDetails(flagKey, fallback, context).Value;
    public EvaluationDetails<string> GetStringDetails(string flagKey, string fallback, EvaluationContext? context = null) => EvaluateTyped(flagKey, "string", fallback, context, element => element.GetString()!);
    public double GetNumberValue(string flagKey, double fallback, EvaluationContext? context = null) => GetNumberDetails(flagKey, fallback, context).Value;
    public EvaluationDetails<double> GetNumberDetails(string flagKey, double fallback, EvaluationContext? context = null) => EvaluateTyped(flagKey, "number", fallback, context, element => element.GetDouble());
    public T GetJsonValue<T>(string flagKey, T fallback, EvaluationContext? context = null) => GetJsonDetails(flagKey, fallback, context).Value;

    public EvaluationDetails<T> GetJsonDetails<T>(string flagKey, T fallback, EvaluationContext? context = null)
    {
        var raw = EvaluateRaw(flagKey, "json", context);
        if (raw.ErrorCode != EvaluationErrorCode.None) return Fallback(fallback, raw);
        try
        {
            var value = raw.Value.Deserialize<T>(ConfigurationParser.JsonOptions);
            if (value is null && fallback is not null)
                return new EvaluationDetails<T>(fallback, "default", EvaluationReason.Error, raw.RuleId, EvaluationErrorCode.TypeMismatch, "SwitchOnYourCode JSON value could not be converted to the requested type.");
            return new EvaluationDetails<T>(value!, raw.Variant, raw.Reason, raw.RuleId);
        }
        catch (JsonException exception)
        {
            return new EvaluationDetails<T>(fallback, "default", EvaluationReason.Error, raw.RuleId, EvaluationErrorCode.TypeMismatch, exception.Message);
        }
    }

    public EvaluationDetails<JsonElement> GetRawDetails(string flagKey, EvaluationContext? context = null)
    {
        var raw = EvaluateRaw(flagKey, null, context);
        var value = raw.Value.ValueKind == JsonValueKind.Undefined ? NullJsonElement() : raw.Value.Clone();
        return new EvaluationDetails<JsonElement>(value, raw.Variant, raw.Reason, raw.RuleId, raw.ErrorCode, raw.ErrorMessage);
    }

    internal RawEvaluationDetails EvaluateRaw(string flagKey, string? expectedKind, EvaluationContext? context)
    {
        FeatureFlag? flag;
        string environmentId;
        List<Segment> segments;
        lock (_stateLock)
        {
            if (_configuration is null) return RawError(EvaluationErrorCode.ProviderNotReady, "SwitchOnYourCode client has no configuration.");
            if (!_flags.TryGetValue(flagKey, out flag)) return RawError(EvaluationErrorCode.FlagNotFound, $"Feature flag '{flagKey}' was not found.");
            if (expectedKind is not null && flag.Kind != expectedKind) return RawError(EvaluationErrorCode.TypeMismatch, $"Feature flag '{flagKey}' is {flag.Kind}, not {expectedKind}.");
            environmentId = _configuration.Environment.Id;
            segments = [.. _configuration.Segments];
        }
        return Evaluator.Evaluate(flag, environmentId, context, segments);
    }

    private EvaluationDetails<T> EvaluateTyped<T>(string flagKey, string expectedKind, T fallback, EvaluationContext? context, Func<JsonElement, T> converter)
    {
        var raw = EvaluateRaw(flagKey, expectedKind, context);
        if (raw.ErrorCode != EvaluationErrorCode.None) return Fallback(fallback, raw);
        try { return new EvaluationDetails<T>(converter(raw.Value), raw.Variant, raw.Reason, raw.RuleId); }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or OverflowException)
        { return new EvaluationDetails<T>(fallback, "default", EvaluationReason.Error, raw.RuleId, EvaluationErrorCode.TypeMismatch, exception.Message); }
    }

    private static EvaluationDetails<T> Fallback<T>(T fallback, RawEvaluationDetails raw) => new(fallback, raw.Variant, raw.Reason, raw.RuleId, raw.ErrorCode, raw.ErrorMessage);
    private static RawEvaluationDetails RawError(EvaluationErrorCode code, string message) => new(default, "default", EvaluationReason.Error, ErrorCode: code, ErrorMessage: message);
    private static JsonElement NullJsonElement() { using var document = JsonDocument.Parse("null"); return document.RootElement.Clone(); }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_pollInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            try { await RefreshAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception exception) { PollingError?.Invoke(exception); }
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (memory.Length + read > MaxConfigurationBytes) throw new SwitchOnYourCodeConfigurationException("SwitchOnYourCode configuration response exceeds 10 MiB.");
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    private static bool ConfigurationsEqual(SwitchOnYourCodeConfiguration left, SwitchOnYourCodeConfiguration right)
    {
        var leftJson = JsonSerializer.SerializeToUtf8Bytes(left, ConfigurationParser.JsonOptions);
        var rightJson = JsonSerializer.SerializeToUtf8Bytes(right, ConfigurationParser.JsonOptions);
        return leftJson.AsSpan().SequenceEqual(rightJson);
    }

    public void Dispose()
    {
        if (_disposed) return;
        StopPollingAsync().GetAwaiter().GetResult();
        if (_ownsHttpClient) _httpClient.Dispose();
        _disposed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await StopPollingAsync().ConfigureAwait(false);
        if (_ownsHttpClient) _httpClient.Dispose();
        _disposed = true;
    }
}
