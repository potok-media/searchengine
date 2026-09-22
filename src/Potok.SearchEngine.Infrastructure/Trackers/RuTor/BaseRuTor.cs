using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.Extensions.Options;
using Potok.SearchEngine.Core.Enums;
using Potok.SearchEngine.Core.Utils;
using Potok.SearchEngine.Infrastructure.Http;
using Potok.SearchEngine.Infrastructure.Trackers.Toolkit;

namespace Potok.SearchEngine.Infrastructure.Trackers.RuTor;

/// <summary>
///     RuTor topic-page (detail) side: fetches table#details, extracts labelled facts and
///     header metadata, scrubs secrets and momentary telemetry, and folds normalized fields
///     back into the torrent. List parsing lives in BaseRuTor.List.cs.
/// </summary>
public partial class BaseRuTor : BaseTrackerSearch
{
    private const string ParserVersion = "rutor/2026-09-22";
    private readonly HtmlParser _parser = new();

    protected BaseRuTor(IOptions<Config> config, TrackerHttpClient httpService, ICacheService cacheService)
        : base(config, httpService, cacheService)
    {
    }

    public override TrackerType Tracker => TrackerType.Rutor;
    public override string TrackerName => "rutor";
    public override string Host => "http://rutor.info/";

    public async Task<bool> FetchDetailsAsync(TorrentDetails torrent, CancellationToken ct)
    {
        if (torrent == null || string.IsNullOrWhiteSpace(torrent.Url))
            return false;
        try
        {
            var html = await HttpService.GetStringAsync(torrent.Url, referer: torrent.Url, ct: ct);
            if (string.IsNullOrWhiteSpace(html))
            {
                TrackerPayload.WithDetails(torrent, ParserVersion, TrackerDetailsState.Failed,
                    TrackerPayload.EmptyObject, TrackerPayload.EmptyObject, ["detail_empty_response"], null);
                return false;
            }

            var document = await _parser.ParseDocumentAsync(html, ct);
            ct.ThrowIfCancellationRequested();
            var detailsTable = document.QuerySelector("table#details");
            if (detailsTable is null)
            {
                TrackerPayload.WithDetails(torrent, ParserVersion, TrackerDetailsState.Failed,
                    TrackerPayload.EmptyObject, TrackerPayload.EmptyObject, ["detail_contract_missing"], null);
                return false;
            }

            var inventory = ParseDetailsTable(detailsTable, torrent);
            TrackerPayload.WithDetails(torrent, ParserVersion,
                inventory.HashMismatch ? TrackerDetailsState.Partial : TrackerDetailsState.Fetched,
                inventory.Details, inventory.Unmapped,
                inventory.Warnings, DateTimeOffset.UtcNow, inventory.ObservedFields);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            TrackerPayload.WithDetails(torrent, ParserVersion, TrackerDetailsState.Failed,
                TrackerPayload.EmptyObject, TrackerPayload.EmptyObject, ["detail_fetch_failed"], null);
            return false;
        }
    }

    protected static bool HasValidIdentity(TorrentDetails torrent)
    {
        var magnetHash = MagnetBuilder.HashFromMagnet(torrent.Magnet);
        return magnetHash is not null &&
               string.Equals(magnetHash, torrent.InfoHash, StringComparison.Ordinal);
    }

    private DetailInventory ParseDetailsTable(IElement detailsTable, TorrentDetails torrent)
    {
        var content = FindReleaseContent(detailsTable);
        var facts = content is null
            ? LabelledFacts.NewDictionary()
            : LabelledFacts.ExtractInline(content);
        var (metadata, categoryHref) = ExtractMetadata(detailsTable);
        var warnings = new List<string>();
        LabelledFacts.RedactSensitive(facts, warnings);
        LabelledFacts.RedactSensitive(metadata, warnings);
        LabelledFacts.RemoveTransientTelemetry(facts);
        LabelledFacts.RemoveTransientTelemetry(metadata);

        var observed = torrent.Source?.ObservedFields ?? TorrentObservedFields.None;
        ApplyNormalizedFacts(torrent, facts, metadata, categoryHref, ref observed);
        var explicitHashRaw = TrackerText.FindFact(facts, "Инфо-хеш", "Инфо хеш", "Info hash", "Hash");
        var explicitHash = MagnetBuilder.NormalizeHash(explicitHashRaw);
        var hashMismatch = explicitHash is not null && torrent.InfoHash is not null &&
                           !string.Equals(explicitHash, torrent.InfoHash, StringComparison.Ordinal);
        if (explicitHashRaw is not null && explicitHash is null)
            warnings.Add("explicit_hash_invalid");
        if (hashMismatch)
        {
            torrent.InfoHash = null;
            torrent.Magnet = null;
            observed |= TorrentObservedFields.InfoHash | TorrentObservedFields.Magnet;
            warnings.Add("hash_mismatch");
        }

        var externalLinks = content?.QuerySelectorAll("a[href]")
            .Select(link => SafeUrl.ToSafePublicUrl(link.GetAttribute("href"), torrent.Url))
            .Where(value => value is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
        var images = content?.QuerySelectorAll("img[src]")
            .Select(image => SafeUrl.ToSafePublicUrl(image.GetAttribute("src"), torrent.Url))
            .Where(value => value is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
        var hiddenSections = content?.QuerySelectorAll(".hidewrap")
            .Select(section => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["title"] = TrackerText.NormalizeText(section.QuerySelector(".hidehead")?.TextContent ?? string.Empty),
                ["content"] = section.QuerySelector("textarea.hidearea")?.TextContent.Trim()
            })
            .Where(section => !string.IsNullOrWhiteSpace(section["content"] as string))
            .ToArray() ?? [];

        var details = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["facts"] = facts,
            ["metadata"] = metadata,
            ["categoryHref"] = categoryHref,
            ["externalLinks"] = externalLinks,
            ["images"] = images,
            ["hiddenSections"] = hiddenSections,
            ["explicitInfoHash"] = explicitHashRaw
        };
        var unmapped = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["labelledFacts"] = facts,
            ["metadata"] = metadata,
            ["categoryHref"] = categoryHref,
            ["externalLinks"] = externalLinks,
            ["images"] = images,
            ["hiddenSections"] = hiddenSections
        };
        return new DetailInventory(details, unmapped, warnings, observed, hashMismatch);
    }

    private static IElement? FindReleaseContent(IElement detailsTable)
    {
        foreach (var row in detailsTable.QuerySelectorAll("tr"))
        {
            var cells = row.Children.Where(element => element.TagName.Equals("TD", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (cells.Length >= 2 && !cells[0].ClassList.Contains("header") &&
                cells[1].QuerySelector("b, strong") is not null)
                return cells[1];
        }
        return null;
    }

    private static (Dictionary<string, string> Facts, string? CategoryHref) ExtractMetadata(IElement detailsTable)
    {
        var metadata = LabelledFacts.NewDictionary();
        string? categoryHref = null;
        foreach (var header in detailsTable.QuerySelectorAll("td.header"))
        {
            var valueCell = header.NextElementSibling;
            if (valueCell is null)
                continue;
            var label = TrackerText.NormalizeText(header.TextContent).TrimEnd(':').Trim();
            LabelledFacts.AddFact(metadata, label, valueCell.TextContent);
            if (label.Equals("Категория", StringComparison.OrdinalIgnoreCase))
                categoryHref = valueCell.QuerySelector("a[href]")?.GetAttribute("href");
        }
        return (metadata, categoryHref);
    }

    private sealed record DetailInventory(
        IReadOnlyDictionary<string, object?> Details,
        IReadOnlyDictionary<string, object?> Unmapped,
        IReadOnlyCollection<string> Warnings,
        TorrentObservedFields ObservedFields,
        bool HashMismatch);
}
