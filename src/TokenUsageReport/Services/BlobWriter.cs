using System.Text;
using Azure.Core;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace TokenUsageReport.Services;

/// <summary>Uploads the CSV report to Blob Storage using the app's managed identity.</summary>
public sealed class BlobWriter
{
    private readonly BlobServiceClient _service;
    private readonly string _container;

    public BlobWriter(string blobEndpoint, string container, TokenCredential credential)
    {
        _service = new BlobServiceClient(new Uri(blobEndpoint), credential);
        _container = container;
    }

    public async Task<Uri> UploadAsync(string blobName, string csv, CancellationToken ct)
    {
        var containerClient = _service.GetBlobContainerClient(_container);
        await containerClient.CreateIfNotExistsAsync(cancellationToken: ct);

        var blob = containerClient.GetBlobClient(blobName);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        await blob.UploadAsync(
            stream,
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = "text/csv" } },
            ct);

        return blob.Uri;
    }
}
