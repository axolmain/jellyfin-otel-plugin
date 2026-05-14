using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Jellyfin.Plugin.Jellytel.LocalBuffer;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;

namespace Jellyfin.Plugin.Jellytel.Metrics.Export;

/// <summary>
/// OTLP/HTTP protobuf exporter built on <see cref="HttpClient"/> and the
/// generated OTLP proto types. Replaces the OpenTelemetry SDK exporter,
/// which cannot load on net9 Jellyfin hosts because OTel 1.13+ requires
/// <c>System.Diagnostics.DiagnosticSource</c> 10.x.
/// </summary>
/// <remarks>
/// Wire format reference: opentelemetry.io/docs/specs/otlp/#otlphttp.
/// We POST a single <c>ExportMetricsServiceRequest</c> per snapshot to
/// <c>{endpoint}/v1/metrics</c> with content-type <c>application/x-protobuf</c>.
/// </remarks>
public sealed class OtlpHttpExporter : IMetricExporter
{
    private const string MeterName = JellytelMeter.Name;
    private const string MeterVersion = "0";

    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly ExporterContext _context;
    private readonly ExportStatusTracker _status;
    private readonly ILogger<OtlpHttpExporter> _logger;
    private readonly bool _ownsHttpClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="OtlpHttpExporter"/> class.
    /// </summary>
    /// <param name="endpoint">Base OTLP endpoint, e.g. <c>http://localhost:4318</c>. <c>/v1/metrics</c> is appended.</param>
    /// <param name="context">Resource-level metadata.</param>
    /// <param name="status">Status tracker for the dashboard panel.</param>
    /// <param name="httpClient">HTTP client (typically from <c>IHttpClientFactory</c>). If null, a dedicated client is created.</param>
    /// <param name="logger">Diagnostic logger.</param>
    public OtlpHttpExporter(
        Uri endpoint,
        ExporterContext context,
        ExportStatusTracker status,
        HttpClient? httpClient,
        ILogger<OtlpHttpExporter> logger)
    {
        _endpoint = new Uri(endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/v1/metrics", UriKind.Absolute);
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
    public string Name => "otlp-http";

    /// <inheritdoc />
    public async Task<bool> ExportAsync(MetricSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (snapshot.Metrics.Count == 0)
        {
            return true;
        }

        try
        {
            var request = BuildRequest(snapshot);
            using var content = new ByteArrayContent(request.ToByteArray());
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");

            using var response = await _http.PostAsync(_endpoint, content, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await SafeReadAsync(response, cancellationToken).ConfigureAwait(false);
                var error = $"HTTP {(int)response.StatusCode}: {body}";
                _status.MarkFailure(error);
                _logger.LogWarning("Jellytel metrics: OTLP export failed. {Error}", error);
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
            _logger.LogWarning(ex, "Jellytel metrics: OTLP export threw.");
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

    private ExportMetricsServiceRequest BuildRequest(MetricSnapshot snapshot)
    {
        var resource = new Resource();
        resource.Attributes.Add(KeyValueOf("service.name", _context.ServiceName));
        foreach (var attr in _context.ResourceAttributes)
        {
            resource.Attributes.Add(KeyValueOf(attr.Key, attr.Value));
        }

        var scope = new InstrumentationScope { Name = MeterName, Version = MeterVersion };
        var scopeMetrics = new ScopeMetrics { Scope = scope };

        foreach (var family in snapshot.Metrics)
        {
            var metric = BuildMetric(family);
            if (metric is not null)
            {
                scopeMetrics.Metrics.Add(metric);
            }
        }

        var resourceMetrics = new ResourceMetrics { Resource = resource };
        resourceMetrics.ScopeMetrics.Add(scopeMetrics);

        var request = new ExportMetricsServiceRequest();
        request.ResourceMetrics.Add(resourceMetrics);
        return request;
    }

    private static Metric? BuildMetric(MetricFamily family)
    {
        var metric = new Metric
        {
            Name = family.Name,
            Description = family.Description,
            Unit = family.Unit,
        };

        switch (family.Kind)
        {
            case InstrumentKind.Counter:
                {
                    var sum = new Sum
                    {
                        AggregationTemporality = AggregationTemporality.Cumulative,
                        IsMonotonic = true,
                    };
                    foreach (var p in family.NumberPoints)
                    {
                        sum.DataPoints.Add(BuildNumberPoint(p));
                    }

                    metric.Sum = sum;
                    break;
                }

            case InstrumentKind.UpDownCounter:
                {
                    var sum = new Sum
                    {
                        AggregationTemporality = AggregationTemporality.Cumulative,
                        IsMonotonic = false,
                    };
                    foreach (var p in family.NumberPoints)
                    {
                        sum.DataPoints.Add(BuildNumberPoint(p));
                    }

                    metric.Sum = sum;
                    break;
                }

            case InstrumentKind.Gauge:
                {
                    var gauge = new Gauge();
                    foreach (var p in family.NumberPoints)
                    {
                        gauge.DataPoints.Add(BuildNumberPoint(p));
                    }

                    metric.Gauge = gauge;
                    break;
                }

            case InstrumentKind.Histogram:
                {
                    var hist = new Histogram { AggregationTemporality = AggregationTemporality.Delta };
                    foreach (var p in family.HistogramPoints)
                    {
                        hist.DataPoints.Add(BuildHistogramPoint(p));
                    }

                    metric.Histogram = hist;
                    break;
                }

            default:
                return null;
        }

        return metric;
    }

    private static NumberDataPoint BuildNumberPoint(NumberPoint p)
    {
        var dp = new NumberDataPoint
        {
            StartTimeUnixNano = ToUnixNano(p.StartTime),
            TimeUnixNano = ToUnixNano(p.Time),
            AsDouble = p.Value,
        };
        foreach (var tag in p.Tags)
        {
            dp.Attributes.Add(KeyValueOf(tag.Key, tag.Value));
        }

        return dp;
    }

    private static HistogramDataPoint BuildHistogramPoint(HistogramPoint p)
    {
        var dp = new HistogramDataPoint
        {
            StartTimeUnixNano = ToUnixNano(p.StartTime),
            TimeUnixNano = ToUnixNano(p.Time),
            Count = (ulong)p.Count,
            Sum = p.Sum,
        };

        if (p.Min.HasValue)
        {
            dp.Min = p.Min.Value;
        }

        if (p.Max.HasValue)
        {
            dp.Max = p.Max.Value;
        }

        foreach (var bound in p.ExplicitBounds)
        {
            dp.ExplicitBounds.Add(bound);
        }

        foreach (var bc in p.BucketCounts)
        {
            dp.BucketCounts.Add((ulong)bc);
        }

        foreach (var tag in p.Tags)
        {
            dp.Attributes.Add(KeyValueOf(tag.Key, tag.Value));
        }

        return dp;
    }

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
