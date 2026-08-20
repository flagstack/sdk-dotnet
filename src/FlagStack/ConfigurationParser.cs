using System.Text.Json;

namespace FlagStack;

internal static class ConfigurationParser
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };

    internal static FlagStackConfiguration Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            var configuration = JsonSerializer.Deserialize<FlagStackConfiguration>(json, JsonOptions)
                ?? throw new FlagStackConfigurationException("FlagStack configuration must be an object.");
            ConfigurationValidator.Validate(configuration);
            return configuration;
        }
        catch (FlagStackConfigurationException)
        {
            throw;
        }
        catch (EvaluationFailure exception)
        {
            throw new FlagStackConfigurationException($"FlagStack configuration is not compatible with the v1 evaluator: {exception.Message}", exception);
        }
        catch (JsonException exception)
        {
            throw new FlagStackConfigurationException($"FlagStack configuration response was not valid JSON: {exception.Message}", exception);
        }
    }

    internal static FlagStackConfiguration Clone(FlagStackConfiguration configuration) =>
        Parse(JsonSerializer.SerializeToUtf8Bytes(configuration, JsonOptions));
}
