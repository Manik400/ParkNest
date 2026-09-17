using Microsoft.AspNetCore.Mvc;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Common;
using ParkNest.Application.Options;
using ParkNest.Application.Users;
using ParkNest.Domain.Common;
using Microsoft.Extensions.Options;

namespace ParkNest.Api.Controllers;

/// <summary>
/// A host proving who they are, which is what stands between their earnings and their bank.
///
/// The cash-out path has always refused an unverified host. This is the half that was missing:
/// without it a host could earn credits and never convert them, and the only way to verify anyone
/// was an UPDATE against the database by hand.
/// </summary>
[ApiController]
[Route("api/kyc")]
public sealed class KycController : ControllerBase
{
    private readonly IKycService _kyc;
    private readonly IPhotoStorage _storage;
    private readonly StorageOptions _storageOptions;

    public KycController(IKycService kyc, IPhotoStorage storage, IOptions<StorageOptions> storageOptions)
    {
        _kyc = kyc;
        _storage = storage;
        _storageOptions = storageOptions.Value;
    }

    /// <summary>Where the caller stands, and why a previous attempt was refused.</summary>
    [HttpGet("me")]
    public async Task<ActionResult<KycStatusView>> Mine(CancellationToken cancellationToken) =>
        Ok(await _kyc.GetMineAsync(cancellationToken));

    /// <summary>Submits identity details for review. Replaces an attempt still waiting.</summary>
    [HttpPost]
    public async Task<ActionResult<KycSubmissionView>> Submit(
        [FromBody] SubmitKyc request,
        CancellationToken cancellationToken) =>
        Ok(await _kyc.SubmitAsync(request, cancellationToken));

    /// <summary>
    /// Uploads the photograph of the document and returns where it was stored, for the submission
    /// that follows.
    ///
    /// Two calls rather than one multipart form carrying both, because the document number must
    /// not travel in a field that gets logged as a form value on its way through a proxy.
    /// </summary>
    [HttpPost("document")]
    [RequestSizeLimit(8 * 1024 * 1024)]
    public async Task<ActionResult<KycDocumentUploadResponse>> UploadDocument(
        IFormFile file,
        CancellationToken cancellationToken)
    {
        if (!_storage.IsConfigured)
        {
            throw new DomainException("Document storage is not configured on this deployment.");
        }

        if (file is null || file.Length == 0)
        {
            throw new DomainException("Attach a photograph of the document.");
        }

        if (file.Length > _storageOptions.MaxPhotoBytes)
        {
            throw new DomainException(
                $"The photograph must be under {_storageOptions.MaxPhotoBytes / (1024 * 1024)} MB.");
        }

        await using var content = file.OpenReadStream();

        // Sniffed rather than trusted, exactly as listing photos are: this file is served back
        // from our own origin, and the declared content type is whatever the caller typed.
        var extension = await ImageContent.SniffExtensionAsync(content, cancellationToken);
        var url = await _storage.SaveAsync(content, extension, cancellationToken);

        return Ok(new KycDocumentUploadResponse(url));
    }
}

public sealed record KycDocumentUploadResponse(string Url);
