using Microsoft.Extensions.Configuration;

namespace Marvin.Api.Configuration;

internal sealed class TelemetryOptions
{
    [ConfigurationKeyName("OTEL_EXPORTER_OTLP_ENDPOINT")]
    public string? Endpoint { get; set; }
}
