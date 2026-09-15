using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using ContosoDashboard.Services;

namespace ContosoDashboard.Controllers;

[ApiController]
[Authorize]
[Route("document-files")]
public class DocumentFilesController : ControllerBase
{
    private readonly IDocumentService _documents;

    public DocumentFilesController(IDocumentService documents) => _documents = documents;

    [HttpGet("{documentId:int}/preview")]
    public Task<IActionResult> Preview(int documentId) => Serve(documentId, inline: true);

    [HttpGet("{documentId:int}/download")]
    public Task<IActionResult> Download(int documentId) => Serve(documentId, inline: false);

    private async Task<IActionResult> Serve(int documentId, bool inline)
    {
        var claim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(claim, out var userId)) return NotFound();
        var result = await _documents.OpenContentAsync(documentId, userId);
        if (result == null) return NotFound();
        Response.Headers["Content-Disposition"] = $"{(inline ? "inline" : "attachment")}; filename=\"{Uri.EscapeDataString(result.Value.Document.OriginalFileName)}\"";
        return File(result.Value.Content, result.Value.Document.FileType, enableRangeProcessing: true);
    }
}