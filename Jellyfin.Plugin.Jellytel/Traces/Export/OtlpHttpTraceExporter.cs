using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Jellyfin.Plugin.Jellytel.LocalBuffer;
using Jellyfin.Plugin.Jellytel.Metrics.Export;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Trace.V1;
using OtlpSpan = OpenTelemetry.Proto.Trace.V1.Span;
using OtlpStatus = OpenTelemetry.Proto.Trace.V1.Status;

namespace Jellyfin.Plugin.Jellytel.Traces.Export;

/// <summary>
/// OTLP/HTTP protobuf trace exporter built on <see cref="HttpClient"/> and the
/// generated OTLP proto types. Replaces the OpenTelemetry SDK exporter, which
/// cannot load on net9 Jellyfin hosts (<c>DiagnosticSource</c> 9/10 conflict).
/// </summary>
/// <remarks>
/// Wire format reference: opentelemetry.io/docs/specs/otlp/#otlphttp. We POST
/// a single <c>ExportTraceServiceRequest</c> per batch to
/// <c>{endpoint}/v1/traces</c> with content-type <c>application/x-protobuf</c>.
/// </remarks>
public sealed class OtlpHttpTraceExporter : ITraceExporter
{
    private const string ScopeName = JellytelActivitySource.Name;
    private const string ScopeVersion = "0";

    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly ExporterContext _context;
    private readonly ExportStatusTracker _status;
    private readonly ILogger<OtlpHttpTraceExporter> _logger;
    private readonly bool _ownsHttpClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="OtlpHttpTraceExporter"/> class.
    /// </summary>
    /// <param name="endpoint">Base OTLP endpoint, e.g. <c>http://localhost:4318</c>. <c>/v1/traces</c> is appended.</param>
    /// <param name="context">Resource-level metadata.</param>
    /// <param name="status">Status tracker for the dashboard panel.</param>
    /// <param name="httpClient">HTTP client (typically from <c>IHttpClientFactory</c>). If null, a dedicated client is created.</param>
    /// <param name="logger">Diagnostic logger.</param>
    public OtlpHttpTraceExporter(
        Uri endpoint,
        ExporterContext context,
        ExportStatusTracker status,
        HttpClient? httpClient,
        ILogger<OtlpHttpTraceExporter> logger)
    {
        _endpoint = new Uri(endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/v1/traces", UriKind.Absolute);
        _context = context;
        _status = status;
        _logger = logger;

        if (httpClient is null)
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            _ownsHttpClient = true;
        }
        else
        {
            _http = httpClient;
            _ownsHttpClient = false;
        }
    }

    /// <inheritdoc />
    public string Name => "otlp-http-traces";

    /// <inheritdoc />
    public async Task<bool> ExportAsync(IReadOnlyList<Activity> spans, CancellationToken cancellationToken)
    {
        if (spans.Count == 0)
        {
            return true;
        }

        try
        {
            var request = BuildRequest(spans);
            using var content = new ByteArrayContent(request.ToByteArray());
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");

            using var response = await _http.PostAsync(_endpoint, content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await SafeReadAsync(response, cancellationToken).ConfigureAwait(false);
                var error = $"HTTP {(int)response.StatusCode}: {body}";
                _status.MarkFailure(error);
                _logger.LogWarning("Jellytel traces: OTLP export failed. {Error}", error);
                return false;
            }

            _status.MarkSuccess();
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _status.MarkFailure(ex.Message);
            _logger.LogWarning(ex, "Jellytel traces: OTLP export threw.");
            return false;
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private ExportTraceServiceRequest BuildRequest(IReadOnlyList<Activity> spans)
    {
        var resource = new Resource();
        resource.Attributes.Add(KeyValueOf("service.name", _context.ServiceName));
        foreach (var attr in _context.ResourceAttributes)
        {
            resource.Attributes.Add(KeyValueOf(attr.Key, attr.Value));
        }

        var scope = new InstrumentationScope { Name = ScopeName, Version = ScopeVersion };
        var scopeSpans = new ScopeSpans { Scope = scope };
        foreach (var activity in spans)
        {
            scopeSpans.Spans.Add(BuildSpan(activity));
        }

        var resourceSpans = new ResourceSpans { Resource = resource };
        resourceSpans.ScopeSpans.Add(scopeSpans);

        var request = new ExportTraceServiceRequest();
        request.ResourceSpans.Add(resourceSpans);
        return request;
    }

    private static OtlpSpan BuildSpan(Activity activity)
    {
        var traceIdBytes = new byte[16];
        activity.TraceId.CopyTo(traceIdBytes);

        var spanIdBytes = new byte[8];
        activity.SpanId.CopyTo(spanIdBytes);

        var span = new OtlpSpan
        {
            TraceId = ByteString.CopyFrom(traceIdBytes),
            SpanId = ByteString.CopyFrom(spanIdBytes),
            Name = activity.DisplayName,
            Kind = MapKind(activity.Kind),
            StartTimeUnixNano = ToUnixNano(activity.StartTimeUtc),
            EndTimeUnixNano = ToUnixNano(activity.StartTimeUtc.Add(activity.Duration)),
            TraceState = activity.TraceStateString ?? string.Empty,
        };

        if (activity.ParentSpanId != default)
        {
            var parentBytes = new byte[8];
            activity.ParentSpanId.CopyTo(parentBytes);
            span.ParentSpanId = ByteString.CopyFrom(parentBytes);
        }

        foreach (var tag in activity.TagObjects)
        {
            span.Attributes.Add(KeyValueOf(tag.Key, tag.Value));
        }

        foreach (var ev in activity.Events)
        {
            var spanEvent = new OtlpSpan.Types.Event
            {
                Name = ev.Name,
                TimeUnixNano = ToUnixNano(ev.Timestamp),
            };
            foreach (var tag in ev.Tags)
            {
                spanEvent.Attributes.Add(KeyValueOf(tag.Key, tag.Value));
            }

            span.Events.Add(spanEvent);
        }

        foreach (var link in activity.Links)
        {
            var linkTraceId = new byte[16];
            link.Context.TraceId.CopyTo(linkTraceId);
            var linkSpanId = new byte[8];
            link.Context.SpanId.CopyTo(linkSpanId);

            var spanLink = new OtlpSpan.Types.Link
            {
                TraceId = ByteString.CopyFrom(linkTraceId),
                SpanId = ByteString.CopyFrom(linkSpanId),
                TraceState = link.Context.TraceState ?? string.Empty,
            };
            if (link.Tags is not null)
            {
                foreach (var tag in link.Tags)
                {
                    spanLink.Attributes.Add(KeyValueOf(tag.Key, tag.Value));
                }
            }

            span.Links.Add(spanLink);
        }

        span.Status = new OtlpStatus
        {
            Code = MapStatus(activity.Status),
            Message = activity.StatusDescription ?? string.Empty,
        };

        return span;
    }

    private static OtlpSpan.Types.SpanKind MapKind(ActivityKind kind) => kind switch
    {
        ActivityKind.Server => OtlpSpan.Types.SpanKind.Server,
        ActivityKind.Client => OtlpSpan.Types.SpanKind.Client,
        ActivityKind.Producer => OtlpSpan.Types.SpanKind.Producer,
        ActivityKind.Consumer => OtlpSpan.Types.SpanKind.Consumer,
        _ => OtlpSpan.Types.SpanKind.Internal,
    };

    private static OtlpStatus.Types.StatusCode MapStatus(ActivityStatusCode status) => status switch
    {
        ActivityStatusCode.Ok => OtlpStatus.Types.StatusCode.Ok,
        ActivityStatusCode.Error => OtlpStatus.Types.StatusCode.Error,
        _ => OtlpStatus.Types.StatusCode.Unset,
    };

    private static KeyValue KeyValueOf(string key, object? value)
    {
        var kv = new KeyValue { Key = key };
        switch (value)
        {
            case null:
                kv.Value = new AnyValue { StringValue = string.Empty };
                break;
            case string s:
                kv.Value = new AnyValue { StringValue = s };
                break;
            case bool b:
                kv.Value = new AnyValue { BoolValue = b };
                break;
            case long l:
                kv.Value = new AnyValue { IntValue = l };
                break;
            case int i:
                kv.Value = new AnyValue { IntValue = i };
                break;
            case double d:
                kv.Value = new AnyValue { DoubleValue = d };
                break;
            case float f:
                kv.Value = new AnyValue { DoubleValue = f };
                break;
            default:
                kv.Value = new AnyValue { StringValue = value.ToString() ?? string.Empty };
                break;
        }

        return kv;
    }

    private static ulong ToUnixNano(DateTimeOffset dt)
    {
        var ns = (dt.ToUnixTimeMilliseconds() * 1_000_000L) + ((dt.UtcTicks % TimeSpan.TicksPerMillisecond) * 100L);
        return (ulong)Math.Max(0, ns);
    }

    private static ulong ToUnixNano(DateTime dt)
        => ToUnixNano(new DateTimeOffset(dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime()));

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return body.Length > 256 ? body[..256] : body;
        }
        catch
        {
            return "<unreadable>";
        }
    }
}
