using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;

namespace AiAgent.Observability;

public static class TelemetryRegistration
{
    public static IServiceCollection AddDamirTelemetry(this IServiceCollection services, ObservabilityOptions options)
    {
        options.Validate();
        if (!options.Enabled) return services;
        services.AddOpenTelemetry().ConfigureResource(resource => resource.Clear().AddService("damir-backend"))
            .WithTracing(traces =>
            {
                traces.AddSource(AgentTelemetry.SourceName, AgentTelemetry.CoreSourceName)
                    .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(options.SamplingRatio)))
                    .AddProcessor(new CorrelationProcessor());
                if (options.CollectorEndpoint is { } collector)
                    traces.AddOtlpExporter("damir-traces", exporter => Configure(exporter, collector, "v1/traces"));
                if (options.LangfuseEndpoint is { } langfuse)
                    traces.AddOtlpExporter("damir-langfuse", exporter =>
                    {
                        Configure(exporter, langfuse, "v1/traces");
                        exporter.Headers = "Authorization=Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(options.PublicKey + ":" + options.SecretKey)) + ",x-langfuse-ingestion-version=4";
                    });
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(AgentTelemetry.SourceName, AgentTelemetry.CoreSourceName);
                if (options.CollectorEndpoint is { } collector)
                    metrics.AddOtlpExporter("damir-metrics", exporter => Configure(exporter, collector, "v1/metrics"));
            });
        return services;
    }

    private static void Configure(OtlpExporterOptions exporter, Uri endpoint, string signal)
    {
        exporter.Endpoint = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/" + signal);
        exporter.Protocol = OtlpExportProtocol.HttpProtobuf;
        exporter.Headers = "";
        exporter.TimeoutMilliseconds = 3000;
        exporter.ExportProcessorType = ExportProcessorType.Batch;
        exporter.BatchExportProcessorOptions = new() { MaxQueueSize = 1024, MaxExportBatchSize = 128, ScheduledDelayMilliseconds = 1000, ExporterTimeoutMilliseconds = 3000 };
        // Never forward exporter authentication to a redirected endpoint.
        exporter.HttpClientFactory = () => new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(3) };
    }

    private sealed class CorrelationProcessor : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity activity) => activity.TraceStateString = null;

        public override void OnStart(Activity activity)
        {
            foreach (var key in new[] { "damir.task.id", "langfuse.session.id", "langfuse.trace.name" })
                for (var parent = activity.Parent; parent is not null; parent = parent.Parent)
                    if (parent.GetTagItem(key) is { } value) { activity.SetTag(key, value); break; }
        }
    }
}
