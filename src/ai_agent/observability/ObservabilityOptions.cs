using System.Globalization;
using System.Text.Json.Serialization;

namespace AiAgent.Observability;

public sealed class ObservabilityOptions
{
    public bool Enabled { get; init; }
    public Uri? CollectorEndpoint { get; init; }
    public Uri? LangfuseEndpoint { get; init; }
    [JsonIgnore] public string? PublicKey { get; init; }
    [JsonIgnore] public string? SecretKey { get; init; }
    public double SamplingRatio { get; init; } = 1;

    public static ObservabilityOptions FromEnvironment(Func<string, string?>? get = null)
    {
        get ??= Environment.GetEnvironmentVariable;
        var enabled = get("DAMIR_OBSERVABILITY_ENABLED");
        if (string.IsNullOrWhiteSpace(enabled) || enabled == "0") return new();
        if (enabled != "1") throw new ArgumentException("DAMIR_OBSERVABILITY_ENABLED must be 0 or 1.");
        var ratio = get("DAMIR_TRACE_SAMPLE_RATIO");
        var options = new ObservabilityOptions
        {
            Enabled = true,
            CollectorEndpoint = Endpoint(get("DAMIR_OTEL_ENDPOINT")),
            LangfuseEndpoint = Endpoint(get("DAMIR_LANGFUSE_ENDPOINT")),
            PublicKey = get("LANGFUSE_PUBLIC_KEY"), SecretKey = get("LANGFUSE_SECRET_KEY"),
            SamplingRatio = string.IsNullOrWhiteSpace(ratio) ? 1 :
                double.TryParse(ratio, CultureInfo.InvariantCulture, out var parsed) ? parsed : double.NaN
        };
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (!Enabled) return;
        if (!double.IsFinite(SamplingRatio) || SamplingRatio is < 0 or > 1)
            throw new ArgumentException("DAMIR_TRACE_SAMPLE_RATIO must be between 0 and 1.");
        if (CollectorEndpoint is null && LangfuseEndpoint is null)
            throw new ArgumentException("Enable a collector or Langfuse endpoint for observability.");
        foreach (var endpoint in new[] { CollectorEndpoint, LangfuseEndpoint }.OfType<Uri>())
            if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https") ||
                endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
                throw new ArgumentException("Telemetry endpoints must be HTTP(S) URLs without credentials, query or fragment.");
        if (LangfuseEndpoint is not null && (string.IsNullOrWhiteSpace(PublicKey) || string.IsNullOrWhiteSpace(SecretKey) ||
            PublicKey.Length > 512 || SecretKey.Length > 512 || PublicKey.Any(char.IsControl) || SecretKey.Any(char.IsControl) || PublicKey.Contains(':')))
            throw new ArgumentException("Langfuse requires valid LANGFUSE_PUBLIC_KEY and LANGFUSE_SECRET_KEY.");
    }

    private static Uri? Endpoint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint)) throw new ArgumentException("Invalid telemetry endpoint.");
        return endpoint;
    }

    public override string ToString() => Enabled ? "Observability enabled" : "Observability disabled";
}
