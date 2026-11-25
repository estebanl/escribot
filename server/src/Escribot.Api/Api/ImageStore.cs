using System.Text.RegularExpressions;
using Escribot.Api.Common;
using Escribot.Api.Data;
using Escribot.Api.Data.Models;
using Escribot.Api.Storage;

namespace Escribot.Api.Api;

/// <summary>
/// An upload that was refused, with the status to answer: 400, 413 for size and allowance,
/// or 403 with <c>upgrade_required</c> for a type the plan does not include.
/// </summary>
public sealed class ImageUploadException(string message, int statusCode = StatusCodes.Status400BadRequest, string? code = null) : Exception(message)
{
    public int StatusCode { get; } = statusCode;

    public string? Code { get; } = code;
}

/// <summary>
/// Uploaded images (Task 1 chart screenshots). The bytes are in Blob Storage, the
/// description in the user's partition, and an image is only ever read for its owner.
/// </summary>
public sealed partial class ImageStore(IImageRepository images, IFileStore files, TimeProvider time, ILogger<ImageStore> logger)
{
    /// <summary>Largest single upload accepted (the browser already compresses to WebP).</summary>
    public const int MaxImageBytes = 2 * 1024 * 1024;

    public const string RoutePrefix = "/api/images/";

    private static readonly HashSet<string> RasterTypes = ["image/png", "image/jpeg", "image/webp", "image/gif"];

    public const string SvgContentType = "image/svg+xml";

    /// <summary>
    /// Stores an image given as a data URL and returns the address the app shows it from.
    /// Enforces the type (SVGs only on plans with <see cref="PlanLimits.CanTransformSvg"/>),
    /// the size limit and the plan's storage allowance.
    /// </summary>
    public async Task<string> StoreAsync(string userId, string plan, string dataUrl, CancellationToken cancellationToken = default)
    {
        var allowSvg = Plans.Defaults[plan].CanTransformSvg;
        if (!allowSvg && IsSvgDataUrl(dataUrl))
        {
            throw new ImageUploadException(
                "Uploading SVG images is available on the Pro plan.",
                StatusCodes.Status403Forbidden,
                "upgrade_required");
        }

        var (contentType, data) = ParseDataUrl(dataUrl, allowSvg);
        if (data.Length > MaxImageBytes)
        {
            throw new ImageUploadException("Images must be 2 MB or smaller.", StatusCodes.Status413PayloadTooLarge);
        }

        if (Plans.Defaults[plan].StorageBytes is { } allowance)
        {
            var used = await images.GetTotalBytesAsync(userId, cancellationToken);
            if (used + data.Length > allowance)
            {
                throw new ImageUploadException(
                    $"You have used your {allowance / 1024 / 1024} MB of image storage. Delete some uploaded prompts or upgrade your plan.",
                    StatusCodes.Status413PayloadTooLarge);
            }
        }

        var id = Guid.NewGuid().ToString();
        var extension = contentType == SvgContentType
            ? "svg"
            : contentType.Split('/')[1].Replace("jpeg", "jpg", StringComparison.Ordinal);
        var blobName = $"users/{userId}/images/{id}.{extension}";

        // The file first: a description without a file would be a broken image. If the
        // description then cannot be saved, the file is removed again, because nothing
        // would point at it: not the app, and not account deletion, which finds a user's
        // files through their descriptions.
        await files.SaveAsync(blobName, data, contentType, cancellationToken);
        try
        {
            await images.SaveAsync(
                userId,
                new ImageMetadata { Id = id, ContentType = contentType, Bytes = data.Length, BlobName = blobName, CreatedAt = time.GetUtcNow() },
                cancellationToken);
        }
        catch
        {
            await files.DeleteAsync(blobName, CancellationToken.None);
            throw;
        }

        return $"{RoutePrefix}{id}";
    }

    /// <summary>
    /// Removes an image that was just stored for something that then could not be saved,
    /// so it does not stay behind unused and counted against the user's allowance.
    /// It never throws: it runs while another failure is already being reported.
    /// </summary>
    public async Task DiscardAsync(string userId, string? imageUrl)
    {
        if (imageUrl?.StartsWith(RoutePrefix, StringComparison.Ordinal) != true)
        {
            return;
        }

        try
        {
            var imageId = imageUrl[RoutePrefix.Length..];
            if (await images.GetAsync(userId, imageId, CancellationToken.None) is { } metadata)
            {
                await files.DeleteAsync(metadata.BlobName, CancellationToken.None);
                await images.DeleteAsync(userId, imageId, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            LogDiscardFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "An image stored for a save that failed could not be removed again.")]
    private static partial void LogDiscardFailed(ILogger logger, Exception exception);

    /// <summary>One of the user's images, or null when it does not exist or is not theirs.</summary>
    public async Task<(string ContentType, byte[] Data)?> GetOwnedAsync(string userId, string imageId, CancellationToken cancellationToken = default)
    {
        var metadata = await images.GetAsync(userId, imageId, cancellationToken);
        if (metadata is null)
        {
            return null;
        }

        var data = await files.ReadAsync(metadata.BlobName, cancellationToken);
        return data is null ? null : (metadata.ContentType, data);
    }

    /// <summary>
    /// A prompt's image as a data URL, for a backup file or an AI vision call. An inline
    /// data URL is returned as it is; anything that is not one of the user's stored images is null.
    /// </summary>
    public async Task<string?> ResolveAsDataUrlAsync(string userId, string? reference, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return null;
        }

        if (reference.StartsWith("data:", StringComparison.Ordinal))
        {
            return reference;
        }

        if (!reference.StartsWith(RoutePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var image = await GetOwnedAsync(userId, reference[RoutePrefix.Length..], cancellationToken);
        return image is { } found ? $"data:{found.ContentType};base64,{Convert.ToBase64String(found.Data)}" : null;
    }

    internal static (string ContentType, byte[] Data) ParseDataUrl(string dataUrl, bool allowSvg = true)
    {
        var match = DataUrl().Match(dataUrl);
        if (!match.Success)
        {
            throw new ImageUploadException("The image could not be read. Try a PNG, JPEG or WebP file.");
        }

        var contentType = match.Groups[1].Value.ToLowerInvariant();
        var isSvg = contentType == SvgContentType;
        if (!RasterTypes.Contains(contentType) && !(allowSvg && isSvg))
        {
            throw new ImageUploadException(allowSvg
                ? "Only PNG, JPEG, WebP, GIF or SVG images can be uploaded."
                : "Only PNG, JPEG, WebP or GIF images can be uploaded.");
        }

        byte[] data;
        try
        {
            data = Convert.FromBase64String(WhiteSpace().Replace(match.Groups[2].Value, string.Empty));
        }
        catch (FormatException)
        {
            throw new ImageUploadException("The image could not be read. Try a PNG, JPEG or WebP file.");
        }

        if (data.Length == 0)
        {
            throw new ImageUploadException("The image is empty.");
        }

        if (isSvg)
        {
            var rawSvg = System.Text.Encoding.UTF8.GetString(data);
            var sanitized = SvgSanitizer.Sanitize(rawSvg)
                ?? throw new ImageUploadException("The SVG image is not valid.");
            return (contentType, System.Text.Encoding.UTF8.GetBytes(sanitized));
        }

        if (!HasValidMagicBytes(contentType, data))
        {
            throw new ImageUploadException("The image file does not match its declared format. Try a PNG, JPEG, WebP or GIF file.");
        }

        return (contentType, data);
    }

    private static ReadOnlySpan<byte> PngMagic => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> JpegMagic => [0xFF, 0xD8, 0xFF];

    private static bool HasValidMagicBytes(string contentType, ReadOnlySpan<byte> data) => contentType switch
    {
        "image/png" => data.StartsWith(PngMagic),
        "image/jpeg" => data.StartsWith(JpegMagic),
        "image/gif" => data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8),
        "image/webp" => data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8),
        _ => false,
    };

    /// <summary>Whether a data URL declares an SVG; <see cref="DataUrl"/> needs <c>;base64,</c> right after the type.</summary>
    private static bool IsSvgDataUrl(string dataUrl) =>
        dataUrl.StartsWith($"data:{SvgContentType};", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^data:([a-z]+/[a-z0-9.+-]+);base64,([A-Za-z0-9+/=\s]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DataUrl();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}
