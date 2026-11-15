namespace FluentEscritor.DataModel;

/// <summary>
/// The Cosmos DB database and containers, defined once. This file is compiled into both
/// the AppHost, which declares them for the emulator and for Azure, and the API, which
/// reads and writes them. See server/docs/data-model.md.
/// </summary>
internal static class DataModelSchema
{
    public const string DatabaseName = "fluentescritor";

    public static readonly ContainerDefinition Users = new("users", "/userId", DefaultTimeToLiveSeconds: null);

    public static readonly ContainerDefinition Shared = new("shared", "/pk", DefaultTimeToLiveSeconds: null);

    /// <summary>Documents never expire unless they carry their own <c>ttl</c> (session tokens do).</summary>
    public static readonly ContainerDefinition Lookups = new("lookups", "/id", DefaultTimeToLiveSeconds: -1);

    /// <summary>Every counter document carries its own <c>ttl</c>.</summary>
    public static readonly ContainerDefinition Counters = new("counters", "/id", DefaultTimeToLiveSeconds: -1);

    /// <summary>Error events are removed by Cosmos DB 30 days after they are written.</summary>
    public static readonly ContainerDefinition Errors = new("errors", "/day", DefaultTimeToLiveSeconds: 30 * 24 * 60 * 60);

    public static readonly IReadOnlyList<ContainerDefinition> Containers = [Users, Shared, Lookups, Counters, Errors];
}

/// <param name="Name">Container name.</param>
/// <param name="PartitionKeyPath">Path of the partition key property.</param>
/// <param name="DefaultTimeToLiveSeconds">
/// Null: time-to-live is off. -1: on, and only documents with their own <c>ttl</c> expire.
/// A positive number: documents expire that many seconds after their last write.
/// </param>
internal sealed record ContainerDefinition(string Name, string PartitionKeyPath, int? DefaultTimeToLiveSeconds);
