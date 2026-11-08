using System.Diagnostics.Metrics;

namespace AxonHub.Api.Tests;

/// <summary>
/// Collects every measurement from one <see cref="Meter"/> through a
/// <see cref="MeterListener"/>, which is the .NET equivalent of the
/// reference's SDK <c>ManualReader</c>.
/// </summary>
internal sealed class MetricsCapture : IDisposable
{
    private readonly MeterListener _listener = new();

    public MetricsCapture(Meter meter)
    {
        ArgumentNullException.ThrowIfNull(meter);

        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, meter))
            {
                Instruments[instrument.Name] = instrument;
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>(OnLong);
        _listener.SetMeasurementEventCallback<double>(OnDouble);
        _listener.Start();
    }

    /// <summary>The instruments published by the captured meter, keyed by name.</summary>
    public Dictionary<string, Instrument> Instruments { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, List<MetricPoint>> Gauges { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, List<MetricPoint>> Counters { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, List<MetricPoint>> Histograms { get; } = new(StringComparer.Ordinal);

    public void RecordObservable() => _listener.RecordObservableInstruments();

    public void Dispose() => _listener.Dispose();

    private void OnLong(
        Instrument instrument,
        long value,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
        => Record(instrument, value, tags);

    private void OnDouble(
        Instrument instrument,
        double value,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
        => Record(instrument, value, tags);

    private void Record(
        Instrument instrument,
        double value,
        ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var bucket = instrument switch
        {
            ObservableGauge<long> => Gauges,
            ObservableGauge<double> => Gauges,
            Histogram<double> => Histograms,
            _ => Counters,
        };

        var tagMap = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            tagMap[tag.Key] = tag.Value;
        }

        if (!bucket.TryGetValue(instrument.Name, out var points))
        {
            bucket[instrument.Name] = points = [];
        }

        points.Add(new MetricPoint(value, tagMap));
    }
}

internal sealed record MetricPoint(double Value, IReadOnlyDictionary<string, object?> Tags);

/// <summary>Returns a fixed elapsed time for every GetTimestamp/GetElapsedTime pair.</summary>
internal sealed class FixedElapsedClock(TimeSpan elapsed) : TimeProvider
{
    public override long GetTimestamp() => 0;

    public override TimeSpan GetElapsedTime(long startingTimestamp) => elapsed;
}
