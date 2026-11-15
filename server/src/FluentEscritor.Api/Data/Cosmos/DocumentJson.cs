using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace FluentEscritor.Api.Data.Cosmos;

/// <summary>How documents are written as JSON.</summary>
internal static class DocumentJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new UtcTimestampConverter() },
    };

    /// <summary>
    /// Timestamps are stored as UTC with exactly three decimals, for example
    /// <c>2026-11-15T18:04:05.123Z</c>, so comparing and sorting them as strings in
    /// queries matches comparing them as times.
    /// </summary>
    public static string Timestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// The current time at the precision it is stored with, so a record handed back
    /// right after a write equals the one read later.
    /// </summary>
    public static DateTimeOffset Now(TimeProvider time)
    {
        var now = time.GetUtcNow();
        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
    }

    public static JsonObject ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Options)!.AsObject();

    public static MemoryStream ToStream(JsonObject node)
    {
        var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            node.WriteTo(writer);
        }

        stream.Position = 0;
        return stream;
    }

    public static async Task<JsonObject> ReadAsync(Stream stream, CancellationToken cancellationToken) =>
        (await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken))!.AsObject();

    private sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(Timestamp(value));
    }
}

/// <summary>Rules for values used as document ids and partition keys.</summary>
internal static class DocumentId
{
    private const int MaxLength = 200;

    /// <summary>
    /// Ids reach the server from the browser, so they are checked before use: Cosmos DB
    /// rejects some characters, and ':' is reserved here to separate a type from an id.
    /// </summary>
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (c is '/' or '\\' or '?' or '#' or ':' || char.IsControl(c) || char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        return true;
    }

    public static string Require(string? value, string name) =>
        IsValid(value) ? value! : throw new ArgumentException("The value is empty, too long, or contains characters that are not allowed in an id.", name);

    /// <summary>
    /// A fixed-length id for a key that can hold any characters (an email address, an IP
    /// address) or that must not be stored at all (a session token).
    /// </summary>
    public static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
