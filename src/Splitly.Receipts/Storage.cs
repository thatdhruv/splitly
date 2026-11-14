using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Splitly.Receipts;

public interface IReceiptStorage
{
    Task SaveAsync(string name, Stream content, string contentType, CancellationToken ct);
    Task<Stream> OpenAsync(string name, CancellationToken ct);
}

/// <summary>Azure Blob Storage (or Azurite locally). Blobs are private; images are only served through the authorized API.</summary>
public sealed class AzureBlobReceiptStorage : IReceiptStorage
{
    private readonly BlobContainerClient _container;

    public AzureBlobReceiptStorage(BlobServiceClient service, string containerName = "receipts") =>
        _container = service.GetBlobContainerClient(containerName);

    public async Task SaveAsync(string name, Stream content, string contentType, CancellationToken ct)
    {
        await _container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct);
        await _container.GetBlobClient(name).UploadAsync(content,
            new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } }, ct);
    }

    public async Task<Stream> OpenAsync(string name, CancellationToken ct) =>
        await _container.GetBlobClient(name).OpenReadAsync(cancellationToken: ct);
}

/// <summary>Plain files on disk, for local development and tests when no blob connection string is configured.</summary>
public sealed class LocalFileReceiptStorage(string root) : IReceiptStorage
{
    public async Task SaveAsync(string name, Stream content, string contentType, CancellationToken ct)
    {
        var path = PathFor(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        await content.CopyToAsync(file, ct);
    }

    public Task<Stream> OpenAsync(string name, CancellationToken ct) =>
        Task.FromResult<Stream>(File.OpenRead(PathFor(name)));

    // Blob names are server-generated GUID paths, but never let a name escape the root regardless.
    private string PathFor(string name)
    {
        var full = Path.GetFullPath(Path.Combine(root, name));
        if (!full.StartsWith(Path.GetFullPath(root), StringComparison.Ordinal))
            throw new ArgumentException("Invalid blob name.", nameof(name));
        return full;
    }
}
