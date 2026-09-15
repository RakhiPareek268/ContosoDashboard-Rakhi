using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ContosoDashboard.Data;
using ContosoDashboard.Models;

namespace ContosoDashboard.Services;

public record DocumentFilters(string? Category = null, int? ProjectId = null, DateTime? From = null, DateTime? To = null);
public record DocumentSummary(int DocumentId, string Title, string Category, DateTime UploadedDate, long FileSize, string FileType, string? ProjectName, string UploaderName, bool CanManage);
public record DocumentViewDetails(Document Document, bool CanManage, bool CanShare);
public record UploadRequest(string Title, string? Description, string Category, string? Tags, int? ProjectId, int? TaskId, string FileName, string ContentType, long FileSize);
public record MetadataUpdateRequest(string Title, string? Description, string Category, string? Tags);
public record DocumentOperationResult(bool Success, string Message, Document? Document = null);
public record DocumentActivityReport(int TotalDocuments, long TotalBytes, IReadOnlyDictionary<string, int> ByFileType, IReadOnlyDictionary<string, int> ByUploader);

public interface IDocumentService
{
    Task<IReadOnlyList<DocumentSummary>> GetMyDocumentsAsync(int userId, DocumentFilters? filters = null);
    Task<IReadOnlyList<DocumentSummary>> GetSharedDocumentsAsync(int userId, DocumentFilters? filters = null);
    Task<IReadOnlyList<DocumentSummary>> GetProjectDocumentsAsync(int projectId, int userId);
    Task<IReadOnlyList<DocumentSummary>> SearchAsync(int userId, string? query, DocumentFilters? filters = null, string sort = "date");
    Task<DocumentViewDetails?> GetAsync(int documentId, int userId);
    Task<DocumentOperationResult> UploadAsync(int userId, UploadRequest request, Stream content, CancellationToken cancellationToken = default);
    Task<DocumentOperationResult> UpdateMetadataAsync(int documentId, int userId, MetadataUpdateRequest request);
    Task<DocumentOperationResult> ReplaceAsync(int documentId, int userId, UploadRequest request, Stream content, CancellationToken cancellationToken = default);
    Task<DocumentOperationResult> DeleteAsync(int documentId, int userId, CancellationToken cancellationToken = default);
    Task<DocumentOperationResult> ShareAsync(int documentId, int userId, int recipientUserId);
    Task<DocumentActivityReport?> GetActivityReportAsync(int userId);
    Task<(Document Document, Stream Content)?> OpenContentAsync(int documentId, int userId);
}

public sealed class DocumentService : IDocumentService
{
    public static readonly string[] Categories =
    ["Project Documents", "Team Resources", "Personal Files", "Reports", "Presentations", "Other"];

    private readonly ApplicationDbContext _context;
    private readonly IFileStorageService _storage;
    private readonly IMalwareScanner _scanner;
    private readonly INotificationService _notifications;
    private readonly DocumentStorageOptions _options;

    public DocumentService(
        ApplicationDbContext context,
        IFileStorageService storage,
        IMalwareScanner scanner,
        INotificationService notifications,
        IOptions<DocumentStorageOptions> options)
    {
        _context = context;
        _storage = storage;
        _scanner = scanner;
        _notifications = notifications;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<DocumentSummary>> GetMyDocumentsAsync(int userId, DocumentFilters? filters = null)
    {
        var documents = await _context.Documents
            .Include(d => d.Project)
            .Include(d => d.Uploader)
            .Where(d => !d.IsDeleted && d.UploaderId == userId)
            .OrderByDescending(d => d.UploadedDate)
            .ToListAsync();
        return ToSummaries(ApplyFilters(documents, filters), userId);
    }

    public async Task<IReadOnlyList<DocumentSummary>> GetSharedDocumentsAsync(int userId, DocumentFilters? filters = null)
    {
        var documents = await _context.Documents
            .Include(d => d.Project)
            .Include(d => d.Uploader)
            .Include(d => d.Shares)
            .Where(d => !d.IsDeleted && d.Shares.Any(s => s.IsActive && s.SharedWithUserId == userId))
            .OrderByDescending(d => d.UploadedDate)
            .ToListAsync();
        return ToSummaries(ApplyFilters(documents, filters), userId);
    }

    public async Task<IReadOnlyList<DocumentSummary>> GetProjectDocumentsAsync(int projectId, int userId)
    {
        if (!await IsProjectMemberAsync(projectId, userId))
        {
            return Array.Empty<DocumentSummary>();
        }

        var documents = await _context.Documents
            .Include(d => d.Project)
            .Include(d => d.Uploader)
            .Where(d => !d.IsDeleted && d.ProjectId == projectId)
            .OrderByDescending(d => d.UploadedDate)
            .ToListAsync();
        return ToSummaries(documents, userId);
    }

    public async Task<IReadOnlyList<DocumentSummary>> SearchAsync(int userId, string? query, DocumentFilters? filters = null, string sort = "date")
    {
        var documents = await _context.Documents
            .Include(d => d.Project)
            .Include(d => d.Uploader)
            .Include(d => d.Shares)
            .Where(d => !d.IsDeleted)
            .OrderByDescending(d => d.UploadedDate)
            .ToListAsync();

        var accessible = new List<Document>();
        foreach (var document in documents)
        {
            if (await CanReadAsync(document, userId))
            {
                accessible.Add(document);
            }
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim();
            accessible = accessible.Where(d =>
                d.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (d.Description?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (d.Tags?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                d.Uploader.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (d.Project?.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
        }

        return ToSummaries(SortDocuments(ApplyFilters(accessible, filters), sort), userId);
    }

    public async Task<DocumentViewDetails?> GetAsync(int documentId, int userId)
    {
        var document = await LoadDocumentAsync(documentId);
        if (document == null || !await CanReadAsync(document, userId))
        {
            return null;
        }

        var canManage = await CanManageAsync(document, userId);
        return new DocumentViewDetails(document, canManage, canManage);
    }

    public async Task<DocumentOperationResult> UploadAsync(int userId, UploadRequest request, Stream content, CancellationToken cancellationToken = default)
    {
        var validation = await ValidateUploadAsync(userId, request);
        if (validation != null) return validation;

        var scan = await _scanner.ScanAsync(content, request.FileName, request.ContentType, cancellationToken);
        if (!scan.IsClean)
        {
            return new DocumentOperationResult(false, scan.FailureReason ?? "The file was rejected by the malware scanner.");
        }

        var relativePath = CreateRelativePath(userId, request.ProjectId, request.FileName);
        try
        {
            await _storage.UploadAsync(content, relativePath, cancellationToken);
            var document = new Document
            {
                Title = request.Title.Trim(), Description = request.Description?.Trim(), Category = request.Category,
                Tags = request.Tags?.Trim(), OriginalFileName = Path.GetFileName(request.FileName), StoredFilePath = relativePath,
                FileSize = request.FileSize, FileType = request.ContentType, UploaderId = userId,
                ProjectId = request.ProjectId, TaskId = request.TaskId
            };
            _context.Documents.Add(document);
            await _context.SaveChangesAsync(cancellationToken);
            await AddActivityAsync(document.DocumentId, userId, "Upload", cancellationToken);
            await NotifyProjectMembersAsync(document, userId);
            return new DocumentOperationResult(true, "Document uploaded successfully.", document);
        }
        catch (Exception exception) when (exception is IOException or DbUpdateException or InvalidOperationException)
        {
            await _storage.DeleteAsync(relativePath, cancellationToken);
            return new DocumentOperationResult(false, "The document could not be saved. No document record was created.");
        }
    }

    public async Task<DocumentOperationResult> UpdateMetadataAsync(int documentId, int userId, MetadataUpdateRequest request)
    {
        var document = await LoadDocumentAsync(documentId);
        if (document == null || !await CanManageAsync(document, userId)) return Failure("You are not authorized to update this document.");
        if (string.IsNullOrWhiteSpace(request.Title) || !Categories.Contains(request.Category)) return Failure("A valid title and category are required.");
        document.Title = request.Title.Trim(); document.Description = request.Description?.Trim(); document.Category = request.Category; document.Tags = request.Tags?.Trim(); document.UpdatedDate = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        await AddActivityAsync(documentId, userId, "MetadataUpdate");
        return Success("Document metadata updated.", document);
    }

    public async Task<DocumentOperationResult> ReplaceAsync(int documentId, int userId, UploadRequest request, Stream content, CancellationToken cancellationToken = default)
    {
        var document = await LoadDocumentAsync(documentId);
        if (document == null || !await CanManageAsync(document, userId)) return Failure("You are not authorized to replace this document.");
        var validation = await ValidateUploadAsync(userId, request, document.ProjectId, document.TaskId);
        if (validation != null) return validation;
        var scan = await _scanner.ScanAsync(content, request.FileName, request.ContentType, cancellationToken);
        if (!scan.IsClean) return Failure(scan.FailureReason ?? "The replacement was rejected by the malware scanner.");
        var replacementPath = CreateRelativePath(userId, document.ProjectId, request.FileName);
        try
        {
            await _storage.UploadAsync(content, replacementPath, cancellationToken);
            var oldPath = document.StoredFilePath;
            document.OriginalFileName = Path.GetFileName(request.FileName); document.StoredFilePath = replacementPath;
            document.FileSize = request.FileSize; document.FileType = request.ContentType; document.UpdatedDate = DateTime.UtcNow;
            await _context.SaveChangesAsync(cancellationToken);
            await _storage.DeleteAsync(oldPath, cancellationToken);
            await AddActivityAsync(documentId, userId, "Replacement", cancellationToken);
            return Success("Document replaced successfully.", document);
        }
        catch (Exception exception) when (exception is IOException or DbUpdateException or InvalidOperationException)
        {
            await _storage.DeleteAsync(replacementPath, cancellationToken);
            return Failure("The replacement failed and the previous file was retained.");
        }
    }

    public async Task<DocumentOperationResult> DeleteAsync(int documentId, int userId, CancellationToken cancellationToken = default)
    {
        var document = await LoadDocumentAsync(documentId);
        if (document == null || !await CanManageAsync(document, userId)) return Failure("You are not authorized to delete this document.");
        _context.Documents.Remove(document);
        await _context.SaveChangesAsync(cancellationToken);
        await _storage.DeleteAsync(document.StoredFilePath, cancellationToken);
        return Success("Document deleted permanently.");
    }

    public async Task<DocumentOperationResult> ShareAsync(int documentId, int userId, int recipientUserId)
    {
        var document = await LoadDocumentAsync(documentId);
        if (document == null || !await CanManageAsync(document, userId)) return Failure("You are not authorized to share this document.");
        if (recipientUserId == userId || !await _context.Users.AnyAsync(u => u.UserId == recipientUserId)) return Failure("The selected recipient is invalid.");
        if (await _context.DocumentShares.AnyAsync(s => s.DocumentId == documentId && s.SharedWithUserId == recipientUserId && s.IsActive)) return Failure("This document is already shared with that user.");
        _context.DocumentShares.Add(new DocumentShare { DocumentId = documentId, SharedWithUserId = recipientUserId });
        await _context.SaveChangesAsync();
        await _notifications.CreateNotificationAsync(new Notification { UserId = recipientUserId, Title = "Document shared with you", Message = $"{document.Title} was shared with you.", Type = NotificationType.SystemAnnouncement, Priority = NotificationPriority.Informational });
        await AddActivityAsync(documentId, userId, "Share", details: $"Shared with user {recipientUserId}");
        return Success("Document shared successfully.");
    }

    public async Task<DocumentActivityReport?> GetActivityReportAsync(int userId)
    {
        if (!await IsAdministratorAsync(userId)) return null;
        var documents = await _context.Documents.Where(d => !d.IsDeleted).Include(d => d.Uploader).ToListAsync();
        return new DocumentActivityReport(documents.Count, documents.Sum(d => d.FileSize), documents.GroupBy(d => d.FileType).ToDictionary(g => g.Key, g => g.Count()), documents.GroupBy(d => d.Uploader.DisplayName).ToDictionary(g => g.Key, g => g.Count()));
    }

    public async Task<(Document Document, Stream Content)?> OpenContentAsync(int documentId, int userId)
    {
        var document = await LoadDocumentAsync(documentId);
        if (document == null || !await CanReadAsync(document, userId)) return null;
        var stream = await _storage.DownloadAsync(document.StoredFilePath);
        await AddActivityAsync(documentId, userId, "Download");
        return (document, stream);
    }

    private async Task<Document?> LoadDocumentAsync(int documentId) => await _context.Documents.Include(d => d.Project).Include(d => d.Uploader).Include(d => d.Shares).FirstOrDefaultAsync(d => d.DocumentId == documentId && !d.IsDeleted);

    private async Task<DocumentOperationResult?> ValidateUploadAsync(int userId, UploadRequest request, int? expectedProjectId = null, int? expectedTaskId = null)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 255 || !Categories.Contains(request.Category)) return Failure("A title and valid category are required.");
        if (request.FileSize <= 0 || request.FileSize > _options.MaxFileSize) return Failure("The file must be larger than zero and no larger than 25 MB.");
        var extension = Path.GetExtension(request.FileName).ToLowerInvariant();
        if (!_options.AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase) || !_options.AllowedMimeTypes.Contains(request.ContentType, StringComparer.OrdinalIgnoreCase)) return Failure("This file type is not supported.");
        if (expectedProjectId.HasValue && request.ProjectId != expectedProjectId) return Failure("The replacement cannot change its project.");
        if (expectedTaskId.HasValue && request.TaskId != expectedTaskId) return Failure("The replacement cannot change its task.");
        if (request.ProjectId.HasValue && !await IsProjectMemberAsync(request.ProjectId.Value, userId)) return Failure("You are not authorized to use that project.");
        if (request.TaskId.HasValue)
        {
            var task = await _context.Tasks.AsNoTracking().FirstOrDefaultAsync(t => t.TaskId == request.TaskId);
            if (task == null || task.ProjectId != request.ProjectId) return Failure("The task and project association is invalid.");
        }
        return null;
    }

    private async Task<bool> CanReadAsync(Document document, int userId)
    {
        if (await IsAdministratorAsync(userId) || document.UploaderId == userId) return true;
        if (document.ProjectId.HasValue && await IsProjectMemberAsync(document.ProjectId.Value, userId)) return true;
        return document.Shares.Any(s => s.IsActive && s.SharedWithUserId == userId);
    }

    private async Task<bool> CanManageAsync(Document document, int userId)
    {
        if (await IsAdministratorAsync(userId) || document.UploaderId == userId) return true;
        return document.ProjectId.HasValue && await IsProjectManagerAsync(document.ProjectId.Value, userId);
    }

    private async Task<bool> IsProjectMemberAsync(int projectId, int userId) => await _context.Projects.AnyAsync(p => p.ProjectId == projectId && (p.ProjectManagerId == userId || p.ProjectMembers.Any(m => m.UserId == userId)));
    private async Task<bool> IsProjectManagerAsync(int projectId, int userId) => await _context.Projects.AnyAsync(p => p.ProjectId == projectId && p.ProjectManagerId == userId);
    private async Task<bool> IsAdministratorAsync(int userId) => await _context.Users.AnyAsync(u => u.UserId == userId && u.Role == UserRole.Administrator);

    private string CreateRelativePath(int userId, int? projectId, string fileName) => $"{userId}/{projectId?.ToString() ?? "personal"}/{Guid.NewGuid():N}{Path.GetExtension(fileName).ToLowerInvariant()}";
    private static DocumentOperationResult Failure(string message) => new(false, message);
    private static DocumentOperationResult Success(string message, Document? document = null) => new(true, message, document);
    private static IEnumerable<Document> ApplyFilters(IEnumerable<Document> documents, DocumentFilters? filters)
    {
        if (filters == null) return documents;
        return documents.Where(d => (filters.Category == null || d.Category == filters.Category) && (!filters.ProjectId.HasValue || d.ProjectId == filters.ProjectId) && (!filters.From.HasValue || d.UploadedDate >= filters.From) && (!filters.To.HasValue || d.UploadedDate <= filters.To));
    }
    private static IEnumerable<Document> SortDocuments(IEnumerable<Document> documents, string sort) => sort switch
    {
        "title" => documents.OrderBy(d => d.Title),
        "category" => documents.OrderBy(d => d.Category).ThenByDescending(d => d.UploadedDate),
        "size" => documents.OrderByDescending(d => d.FileSize),
        _ => documents.OrderByDescending(d => d.UploadedDate)
    };
    private static IReadOnlyList<DocumentSummary> ToSummaries(IEnumerable<Document> documents, int userId) => documents.Select(d => new DocumentSummary(d.DocumentId, d.Title, d.Category, d.UploadedDate, d.FileSize, d.FileType, d.Project?.Name, d.Uploader.DisplayName, d.UploaderId == userId || d.Project?.ProjectManagerId == userId)).ToList();
    private async Task AddActivityAsync(int documentId, int actorUserId, string type, CancellationToken cancellationToken = default, string? details = null) { _context.DocumentActivities.Add(new DocumentActivity { DocumentId = documentId, ActorUserId = actorUserId, ActivityType = type, Details = details }); await _context.SaveChangesAsync(cancellationToken); }
    private async Task NotifyProjectMembersAsync(Document document, int uploaderId)
    {
        if (!document.ProjectId.HasValue) return;
        var recipients = await _context.ProjectMembers.Where(m => m.ProjectId == document.ProjectId && m.UserId != uploaderId).Select(m => m.UserId).ToListAsync();
        foreach (var recipient in recipients) await _notifications.CreateNotificationAsync(new Notification { UserId = recipient, Title = "New project document", Message = $"A new document was added to project {document.Project?.Name ?? document.ProjectId.ToString()}.", Type = NotificationType.SystemAnnouncement, Priority = NotificationPriority.Informational });
    }
}