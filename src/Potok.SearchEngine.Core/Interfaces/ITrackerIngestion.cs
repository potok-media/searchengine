namespace Potok.SearchEngine.Core.Interfaces;

public interface ITrackerIngestion
{
    IAsyncEnumerable<TrackerIngestionBatch> IngestAsync(
        TrackerIngestionRequest request,
        CancellationToken ct = default);
}
