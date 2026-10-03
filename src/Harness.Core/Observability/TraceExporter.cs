// Harness.WinUI — Licensed under the MIT License.

using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Harness.Core.Observability;

/// <summary>Where to send telemetry over OTLP (Aspire Dashboard, Langfuse, an OpenTelemetry Collector…).</summary>
/// <param name="Endpoint">For HTTP, the base URL (/v1/traces and /v1/metrics are added); for gRPC, the address.</param>
/// <param name="Headers">"name=value" pairs separated by commas, e.g. an Authorization header; may be empty.</param>
/// <param name="Metrics">Also send the GenAI metrics (token usage, operation duration) — not every backend accepts them.</param>
public sealed record OtlpSettings(string Endpoint, bool Grpc, string? Headers, bool Metrics);

/// <summary>
/// Exports the same spans the local recorder sees, over OTLP, to a backend the user set up. Off unless
/// configured. The raw HTTP exchange spans stay local (they repeat the model calls' content in bulk);
/// message content follows <see cref="Telemetry.CaptureContent"/>.
/// </summary>
public sealed class TraceExporter : IDisposable
{
    private TracerProvider? _tracer;
    private MeterProvider? _meter;

    public bool IsExporting => _tracer is not null;

    /// <summary>Starts exporting to <paramref name="settings"/>, or stops when null. Flushes whatever the previous setup still held.</summary>
    public void Configure(OtlpSettings? settings)
    {
        Shutdown();
        if (settings is null)
            return;

        var version = typeof(TraceExporter).Assembly.GetName().Version?.ToString(3);
        var resource = ResourceBuilder.CreateDefault().AddService("Harness.WinUI", serviceVersion: version);

        void Options(OtlpExporterOptions options, string signal)
        {
            options.Protocol = settings.Grpc ? OtlpExportProtocol.Grpc : OtlpExportProtocol.HttpProtobuf;
            options.Endpoint = settings.Grpc ? new Uri(settings.Endpoint) : SignalUri(settings.Endpoint, signal);
            if (!string.IsNullOrWhiteSpace(settings.Headers))
                options.Headers = settings.Headers;
        }

        _tracer = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(resource)
            .AddSource(Telemetry.SourceName)
            .AddProcessor(new KeepHttpLocal())
            .AddOtlpExporter(o => Options(o, "v1/traces"))
            .Build();

        if (settings.Metrics)
        {
            _meter = Sdk.CreateMeterProviderBuilder()
                .SetResourceBuilder(resource)
                .AddMeter(Telemetry.SourceName)
                .AddOtlpExporter(o => Options(o, "v1/metrics"))
                .Build();
        }
    }

    /// <summary>The HTTP exporter takes the full URL of each signal; accept a base URL or one that already names the signal.</summary>
    private static Uri SignalUri(string endpoint, string signal)
    {
        var trimmed = endpoint.Trim().TrimEnd('/');
        foreach (var known in new[] { "/v1/traces", "/v1/metrics" })
        {
            if (trimmed.EndsWith(known, StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed[..^known.Length];
        }
        return new Uri($"{trimmed}/{signal}");
    }

    private void Shutdown()
    {
        _tracer?.ForceFlush(2000);
        _tracer?.Dispose();
        _tracer = null;
        _meter?.Dispose();
        _meter = null;
    }

    public void Dispose() => Shutdown();

    /// <summary>Leaves the raw HTTP spans out of the export; the local recorder still has them.</summary>
    private sealed class KeepHttpLocal : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity activity)
        {
            if (activity.OperationName == Telemetry.OpHttp)
                activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
        }
    }
}
