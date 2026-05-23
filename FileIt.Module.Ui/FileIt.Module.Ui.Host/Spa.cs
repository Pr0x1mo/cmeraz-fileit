using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Azure.Functions.Worker;

namespace FileIt.Module.Ui.Host;

public class Spa
{
    private static readonly FileExtensionContentTypeProvider _mime = new();

    [Function("Spa")]
    public IActionResult Serve(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "ui/{*path}")] HttpRequest req,
        string? path)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var requested = string.IsNullOrEmpty(path) ? "index.html" : path;
        var full = Path.GetFullPath(Path.Combine(root, requested));

        if (!full.StartsWith(root) || !File.Exists(full))
        {
            full = Path.Combine(root, "index.html");
            if (!File.Exists(full)) return new NotFoundResult();
        }

        if (!_mime.TryGetContentType(full, out var contentType))
            contentType = "application/octet-stream";

        return new FileContentResult(File.ReadAllBytes(full), contentType);
    }
}