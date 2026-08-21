using ParkNest.Domain.Common;

namespace ParkNest.Application.Common;

/// <param name="Content">Read once, streamed straight to storage rather than buffered in memory.</param>
public sealed record PhotoUpload(Stream Content, long Length);

/// <summary>
/// What the platform is willing to accept as a picture.
///
/// Shared by every upload path — listing photos, KYC documents, dispute evidence — because the
/// rule is about what we are prepared to store and serve, not about which feature asked. Three
/// copies of this would eventually disagree, and the copy that drifted would be the one that let
/// something through.
/// </summary>
public static class ImageContent
{
    /// <summary>
    /// Formats we store, identified by the bytes that actually start the file.
    ///
    /// Sniffed, never taken from the request. Content-Type is whatever the caller typed, and a
    /// file we serve back from our own origin under a name we chose is exactly the shape of a
    /// stored-XSS bug — an "image/jpeg" full of HTML would be handed to a browser as our content.
    /// </summary>
    private static readonly (byte[] Magic, string Extension)[] AllowedFormats =
    {
        (new byte[] { 0xFF, 0xD8, 0xFF }, ".jpg"),
        (new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, ".png"),
        // WEBP is a RIFF container: "RIFF", four size bytes, then "WEBP" — so the tag that
        // identifies it does not sit at the start of the file.
        (new byte[] { 0x52, 0x49, 0x46, 0x46 }, ".webp"),
    };

    private const int SniffLength = 12;

    /// <summary>
    /// Reads the leading bytes and returns the extension for the format they identify, rewinding
    /// afterwards so the caller can stream the whole file to storage.
    /// </summary>
    public static async Task<string> SniffExtensionAsync(Stream content, CancellationToken cancellationToken)
    {
        if (!content.CanSeek)
        {
            throw new DomainException("That upload could not be read.");
        }

        var header = new byte[SniffLength];
        var read = await content.ReadAtLeastAsync(header, SniffLength, throwOnEndOfStream: false, cancellationToken);
        content.Position = 0;

        foreach (var (magic, extension) in AllowedFormats)
        {
            if (read < magic.Length || !header.AsSpan(0, magic.Length).SequenceEqual(magic))
            {
                continue;
            }

            // RIFF alone is also WAV and AVI. The four bytes at offset 8 are what say it is an
            // image, and without that check we would happily store an audio file as a .webp.
            if (extension == ".webp"
                && (read < SniffLength || !header.AsSpan(8, 4).SequenceEqual("WEBP"u8)))
            {
                continue;
            }

            return extension;
        }

        throw new DomainException("Only JPEG, PNG and WebP images are accepted.");
    }
}
