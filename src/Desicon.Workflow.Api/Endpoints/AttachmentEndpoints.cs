using Desicon.Workflow.Api.Http;
using Desicon.Workflow.Core.Engine;
using Desicon.Workflow.Api.Security;
using Desicon.Workflow.Domain.Requests;
using Desicon.Workflow.Infrastructure.Attachments;
using Desicon.Workflow.Infrastructure.Persistence;
using Desicon.Workflow.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Desicon.Workflow.Api.Endpoints;

/// <summary>
/// Receipts and supporting documents.
/// </summary>
/// <remarks>
/// The evidence of what was purchased. A retirement is not complete without
/// it: the Accounts Officer posting it in Business Central needs to see what
/// the money bought, and until this existed she had a tri-state tick box and
/// nothing behind it.
///
/// Bytes go through the API rather than direct to storage with a SAS. That
/// costs some throughput on large files and buys one thing worth more: read
/// access to an attachment is decided by exactly the same ReadAccessScope that
/// decides read access to the request. A SAS would be a second path to the
/// same data, and two access-control mechanisms that must agree eventually do
/// not.
/// </remarks>
public static class AttachmentEndpoints
{
    /// <summary>
    /// What an approver can reasonably be asked to open.
    /// </summary>
    /// <remarks>
    /// An allowlist, not a blocklist. The set of dangerous types is unbounded
    /// and grows; the set of things a receipt actually is does not. HTML and
    /// SVG are absent deliberately — both execute script, and a file served
    /// from the application's own origin executes as the application.
    /// BlobAttachmentStore also forces Content-Disposition: attachment, so
    /// this is the second of two defences rather than the only one.
    /// </remarks>
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "image/jpeg",
        "image/png",
        "image/heic",
        "image/tiff"
    };

    /// <summary>
    /// 10 MB. A photograph of a receipt taken on a phone is well under this;
    /// anything above it is unlikely to be a receipt, and an upload endpoint
    /// with no ceiling is a way to fill a storage account.
    /// </summary>
    private const long MaxBytes = 10 * 1024 * 1024;

    public static void MapAttachmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/requests/{id:guid}/attachments").RequireAuthorization();

        group.MapPost("/", UploadAsync).DisableAntiforgery();
        group.MapGet("/", ListAsync);
        group.MapGet("/{attachmentId:guid}", DownloadAsync);
    }

    private static async Task<IResult> UploadAsync(
        Guid id,
        IFormFile file,
        HttpRequest httpRequest,
        WorkflowDbContext db,
        IAttachmentStore store,
        ReadAccessScope readAccess,
        ICurrentUserAccessor currentUser,
        IWorkflowClock clock,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return ProblemResults.BadRequest("A file is required.", httpRequest.Path);
        }

        if (file.Length > MaxBytes)
        {
            return ProblemResults.BadRequest(
                $"'{file.FileName}' is {file.Length / 1024 / 1024} MB. The limit is {MaxBytes / 1024 / 1024} MB.",
                httpRequest.Path);
        }

        if (!AllowedContentTypes.Contains(file.ContentType))
        {
            return ProblemResults.BadRequest(
                $"'{file.ContentType}' is not an accepted file type. Attach a PDF or a photograph.",
                httpRequest.Path);
        }

        var request = await db.Requests.FirstOrDefaultAsync(r => r.RequestId == id, cancellationToken);
        if (request is null)
        {
            return ProblemResults.NotFound("Request", id, httpRequest.Path);
        }

        var employee = await currentUser.GetEmployeeAsync(cancellationToken);
        var roles = currentUser.GetRoles();

        if (!await readAccess.CanReadAsync(request, employee, roles, cancellationToken))
        {
            return ProblemResults.Forbidden("You do not have access to this request.", httpRequest.Path);
        }

        // Closed means closed. Attaching to a settled claim would change what
        // an approver signed off after they signed it off, which is precisely
        // what the audit chain exists to make impossible elsewhere.
        if (request.ClosedAt is not null)
        {
            return ProblemResults.BadRequest(
                "This request is closed. Evidence cannot be added after it has been settled.",
                httpRequest.Path);
        }

        var attachmentId = Guid.NewGuid();

        await using var content = file.OpenReadStream();
        var stored = await store.SaveAsync(id, attachmentId, file.ContentType, content, cancellationToken);

        var attachment = new Attachment
        {
            AttachmentId = attachmentId,
            RequestId = id,
            // Trimmed to the leaf: browsers on some platforms send a full path.
            FileName = Path.GetFileName(file.FileName),
            ContentType = file.ContentType,
            SizeBytes = stored.SizeBytes,
            BlobPath = stored.BlobPath,
            Sha256 = stored.Sha256,
            UploadedByUserId = employee.Id,
            UploadedAt = clock.UtcNow
        };

        db.Attachments.Add(attachment);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Created(
            $"/api/v1/requests/{id}/attachments/{attachmentId}",
            ToDto(attachment));
    }

    private static async Task<IResult> ListAsync(
        Guid id,
        HttpRequest httpRequest,
        WorkflowDbContext db,
        ReadAccessScope readAccess,
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken)
    {
        var request = await db.Requests.AsNoTracking()
            .FirstOrDefaultAsync(r => r.RequestId == id, cancellationToken);

        if (request is null)
        {
            return ProblemResults.NotFound("Request", id, httpRequest.Path);
        }

        var employee = await currentUser.GetEmployeeAsync(cancellationToken);
        var roles = currentUser.GetRoles();

        if (!await readAccess.CanReadAsync(request, employee, roles, cancellationToken))
        {
            return ProblemResults.Forbidden("You do not have access to this request.", httpRequest.Path);
        }

        var attachments = await db.Attachments.AsNoTracking()
            .Where(a => a.RequestId == id)
            .OrderBy(a => a.UploadedAt)
            .ToListAsync(cancellationToken);

        return Results.Ok(attachments.Select(ToDto));
    }

    private static async Task<IResult> DownloadAsync(
        Guid id,
        Guid attachmentId,
        HttpRequest httpRequest,
        WorkflowDbContext db,
        IAttachmentStore store,
        ReadAccessScope readAccess,
        ICurrentUserAccessor currentUser,
        CancellationToken cancellationToken)
    {
        var attachment = await db.Attachments.AsNoTracking()
            .FirstOrDefaultAsync(a => a.AttachmentId == attachmentId && a.RequestId == id, cancellationToken);

        if (attachment is null)
        {
            return ProblemResults.NotFound("Attachment", attachmentId, httpRequest.Path);
        }

        var request = await db.Requests.AsNoTracking()
            .FirstOrDefaultAsync(r => r.RequestId == id, cancellationToken);

        if (request is null)
        {
            return ProblemResults.NotFound("Request", id, httpRequest.Path);
        }

        var employee = await currentUser.GetEmployeeAsync(cancellationToken);
        var roles = currentUser.GetRoles();

        // The same check the request itself gets. An attachment is not a
        // separate thing to be permissioned separately -- it is part of the
        // claim, and anyone who can read the claim can read its evidence.
        if (!await readAccess.CanReadAsync(request, employee, roles, cancellationToken))
        {
            return ProblemResults.Forbidden("You do not have access to this request.", httpRequest.Path);
        }

        var stream = await store.OpenAsync(attachment.BlobPath, cancellationToken);

        // Always as a download, never inline, whatever the type says.
        return Results.File(stream, attachment.ContentType, attachment.FileName);
    }

    private static object ToDto(Attachment a) => new
    {
        a.AttachmentId,
        a.FileName,
        a.ContentType,
        a.SizeBytes,
        a.UploadedByUserId,
        a.UploadedAt,

        // Surfaced so the file an auditor downloads can be shown to be the
        // file that was uploaded, without anyone having to take that on trust.
        a.Sha256
    };
}
