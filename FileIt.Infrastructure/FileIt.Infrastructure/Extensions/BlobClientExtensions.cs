using Azure.Storage.Blobs;

namespace FileIt.Infrastructure.Extensions;

public static class BlobClientExtensions
{
    // Resolve the correlation id for a blob, in priority order:
    // 1. Blob metadata "correlationId" (set by an upstream caller like the operator UI
    //    so the whole flow shares one id end to end).
    // 2. The x-ms-client-request-id response header (set when the upload used a client
    //    request id).
    // 3. A fresh GUID as a last resort so a flow always has some id.
    public static async Task<string> GetCorrelationId(this BlobClient blobClient)
    {
        var propsResponse = await blobClient.GetPropertiesAsync();
        var props = propsResponse.Value;

        if (props.Metadata != null
            && props.Metadata.TryGetValue("correlationId", out var metaId)
            && !string.IsNullOrWhiteSpace(metaId))
        {
            return metaId;
        }

        var rawResponse = propsResponse.GetRawResponse();
        if (rawResponse.Headers.TryGetValue("x-ms-client-request-id", out var headerId)
            && !string.IsNullOrWhiteSpace(headerId))
        {
            return headerId!;
        }

        return Guid.NewGuid().ToString();
    }
}