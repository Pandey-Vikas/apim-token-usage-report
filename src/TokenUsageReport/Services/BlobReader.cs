using Azure;
using Azure.Core;
using Azure.Storage.Blobs;

namespace TokenUsageReport.Services;

/// <summary>Lists and reads report blobs using the app's managed identity.</summary>
public sealed class BlobReader
{
    private readonly BlobContainerClient? _container;

    public BlobReader(string? blobEndpoint, string container, TokenCredential credential)
    {
        if (!string.IsNullOrWhiteSpace(blobEndpoint))
        {
            var service = new BlobServiceClient(new Uri(blobEndpoint), credential);
            _container = service.GetBlobContainerClient(container);
        }
    }

    public bool Enabled => _container is not null;

    public async Task<List<BlobInfo>> ListAsync(CancellationToken ct)
    {
        var list = new List<BlobInfo>();
        if (_container is null) return list;

        if (!await _container.ExistsAsync(ct)) return list;

        await foreach (var b in _container.GetBlobsAsync(cancellationToken: ct))
        {
            if (!b.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new BlobInfo(
                b.Name,
                b.Properties.ContentLength ?? 0,
                b.Properties.LastModified?.UtcDateTime));
        }
        return list.OrderByDescending(b => b.Name).ToList();
    }

    public async Task<string?> DownloadTextAsync(string name, CancellationToken ct)
    {
        if (_container is null) return null;
        var blob = _container.GetBlobClient(name);
        try
        {
            var resp = await blob.DownloadContentAsync(ct);
            return resp.Value.Content.ToString();
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }
}

public sealed record BlobInfo(string Name, long Size, DateTime? Modified);
