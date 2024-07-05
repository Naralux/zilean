namespace Zilean.DmmScraper.Features.Dmm;

public partial class DmmPageProcessor(
    DmmSyncState state,
    ILogger<DmmPageProcessor> logger,
    CancellationToken cancellationToken)
    : IDisposable
{
    [GeneratedRegex("""<iframe src="https:\/\/debridmediamanager.com\/hashlist#(.*)"></iframe>""")]
    private static partial Regex HashCollectionMatcher();

    public async Task<List<ExtractedDmmEntry>> ProcessPageAsync(string filePath, string filenameOnly)
    {
        if (state.ParsedPages.TryGetValue(filenameOnly, out _) || !File.Exists(filePath))
        {
            return [];
        }

        var pageSource = await File.ReadAllTextAsync(filePath, cancellationToken);

        var match = HashCollectionMatcher().Match(pageSource);

        if (!match.Success)
        {
            return [];
        }

        try
        {
            var decodedJson = Decompressor.FromEncodedUriComponent(match.Groups[1].Value);
            var byteArray = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetByteCount(decodedJson));

            try
            {
                var byteCount = Encoding.UTF8.GetBytes(decodedJson, byteArray);
                using var memoryStream = new MemoryStream(byteArray, 0, byteCount);
                using var json = await JsonDocument.ParseAsync(memoryStream, cancellationToken: cancellationToken);

                var torrents = json.RootElement.EnumerateArray().Select(ParsePageContent).OfType<ExtractedDmmEntry>().ToList();

                if (torrents.Count == 0)
                {
                    logger.LogInformation("No torrents found in {Filename}", filenameOnly);
                    state.ParsedPages.TryAdd(filenameOnly, 0);
                    return [];
                }

                var sanitizedTorrents = torrents
                    .Where(x=>x.InfoHash.Length == 40 && x.Filesize > 0)
                    .GroupBy(x => x.InfoHash)
                    .Select(group => group.FirstOrDefault())
                    .OfType<ExtractedDmmEntry>()
                    .ToList();

                logger.LogInformation("Parsed {Count} torrents for {Filename}", sanitizedTorrents.Count, filenameOnly);

                return sanitizedTorrents;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(byteArray);
            }
        }
        catch
        {
            state.ParsedPages.TryAdd(filenameOnly, 0);
            return [];
        }
    }

    private static ExtractedDmmEntry? ParsePageContent(JsonElement item) =>
        item.TryGetProperty("filename", out var filenameElement) &&
        item.TryGetProperty("bytes", out var filesizeElement) &&
        item.TryGetProperty("hash", out var hashElement)
            ? new ExtractedDmmEntry(hashElement.GetString(), filenameElement.GetString(), filesizeElement.GetInt64())
            : null;

    public void Dispose()
    {
        GC.Collect();
        GC.SuppressFinalize(this);
    }
}