using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Potok.SearchEngine.Core.Interfaces;
using Potok.SearchEngine.Core.Models;
using Potok.SearchEngine.Core.Models.Api;
using Potok.SearchEngine.Core.Models.Details;

namespace Potok.SearchEngine.Controllers;

[ApiController]
[Route("api/v1/torrents")]
public class TorrentsController : ControllerBase
{
    private readonly ISearchService _searchService;
    private readonly ISeasonOverrideRepository _overrides;
    private readonly Serilog.ILogger _logger;

    public TorrentsController(ISearchService searchService, ISeasonOverrideRepository overrides, Serilog.ILogger logger)
    {
        _searchService = searchService;
        _overrides = overrides;
        _logger = logger;
    }

    private static readonly JsonSerializerOptions StreamJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [HttpPost("search")]
    public async Task<ActionResult<TorrentSearchResponse>> Search([FromBody] TorrentSearchRequest request)
    {
        LogSearchRequest(request, stream: false);
        var results = await _searchService.SearchTorrentsAsync(ToQuery(request), HttpContext.RequestAborted);
        var sharedResults = results.Select(ToSearchResult).ToList();
        await AttachOverridesAsync(sharedResults);
        return Ok(new TorrentSearchResponse(sharedResults));
    }

    [HttpPost("search/stream")]
    public async Task SearchStream([FromBody] TorrentSearchRequest request)
    {
        LogSearchRequest(request, stream: true);
        var ct = HttpContext.RequestAborted;
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "application/x-ndjson; charset=utf-8";
        Response.Headers.CacheControl = "no-cache, no-store";
        Response.Headers["X-Accel-Buffering"] = "no";
        await Response.StartAsync(ct);

        await foreach (var batch in _searchService.SearchTorrentsStreamAsync(ToQuery(request), ct))
        {
            if (batch.Results.Count == 0)
                continue;

            var shared = batch.Results.Select(ToSearchResult).ToList();
            await AttachOverridesAsync(shared);
            await WriteNdjsonAsync(new TorrentSearchStreamEvent("batch", batch.Source, shared), ct);
        }

        await WriteNdjsonAsync(new TorrentSearchStreamEvent("done"), ct);
    }

    private async Task WriteNdjsonAsync(TorrentSearchStreamEvent evt, CancellationToken ct)
    {
        await JsonSerializer.SerializeAsync(Response.Body, evt, StreamJson, ct);
        await Response.Body.WriteAsync("\n"u8.ToArray(), ct);
        await Response.Body.FlushAsync(ct);
    }

    private void LogSearchRequest(TorrentSearchRequest request, bool stream) =>
        _logger.Information(
            "Search request ({Mode}): query='{Query}' title='{Title}' originalTitle='{OriginalTitle}' englishTitle='{EnglishTitle}' year={Year} mediaType={MediaType} id={Id} forceSearch={ForceSearch}",
            stream ? "stream" : "plain",
            request.Query,
            request.Title,
            request.OriginalTitle,
            request.EnglishTitle,
            request.Year,
            request.MediaType,
            request.Id,
            request.ForceSearch ?? false);

    private static TorrentSearchQuery ToQuery(TorrentSearchRequest request) => new()
    {
        TmdbId = request.Id,
        Query = request.Query,
        Title = request.Title ?? request.Query,
        TitleOriginal = FirstNonEmpty(request.OriginalTitle, request.EnglishTitle),
        Year = int.TryParse(request.Year, out var y) ? y : 0,
        IsSerial = request.MediaType == "tv" ? 2 : 1,
        ForceSearch = request.ForceSearch ?? false
    };

    private static TorrentSearchResult ToSearchResult(TorrentDetails r)
    {
        var tags = new List<TorrentTag>();
        if (r.ParsedInfo != null)
        {
            if (!string.IsNullOrEmpty(r.ParsedInfo.Resolution)) tags.Add(new TorrentTag("quality", r.ParsedInfo.Resolution));
            if (!string.IsNullOrEmpty(r.ParsedInfo.Quality)) tags.Add(new TorrentTag("source", r.ParsedInfo.Quality));
            if (!string.IsNullOrEmpty(r.ParsedInfo.Codec)) tags.Add(new TorrentTag("codec", r.ParsedInfo.Codec));
            if (r.ParsedInfo.Year > 0) tags.Add(new TorrentTag("year", r.ParsedInfo.Year.ToString()));
            if (!string.IsNullOrEmpty(r.ParsedInfo.Audio)) tags.Add(new TorrentTag("voice", r.ParsedInfo.Audio));
        }

        return new TorrentSearchResult(
            Id: r.InfoHash ?? r.Url ?? Guid.NewGuid().ToString(),
            Title: r.Title,
            Tracker: r.TrackerName,
            SizeBytes: (long)r.Size,
            Seeders: r.Sid,
            Leechers: r.Pir,
            PublishDate: r.CreateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            MagnetUri: r.Magnet,
            Link: r.Url,
            Tags: tags
        );
    }

    private async Task AttachOverridesAsync(List<TorrentSearchResult> results)
    {
        var hashes = results.Select(r => r.Id).Where(IsInfoHash).ToArray();
        var summaries = await _overrides.GetSummariesAsync(hashes);
        if (summaries.Count == 0) return;

        for (var i = 0; i < results.Count; i++)
        {
            var summary = summaries.GetValueOrDefault(results[i].Id.ToLower());
            if (summary is not null)
                results[i] = results[i] with { Override = summary };
        }
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }

    // Infohashes stored on torrent_overrides are 40-char hex; skip Guid/url fallbacks used as Id.
    private static bool IsInfoHash(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length != 40) return false;
        foreach (var c in id)
        {
            if (!char.IsAsciiHexDigit(c)) return false;
        }
        return true;
    }
}
