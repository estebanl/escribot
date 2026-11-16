using Azure.Provisioning.CosmosDB;
using FluentEscritor.DataModel;

var builder = DistributedApplication.CreateBuilder(args);

// No secret has a value in the repository. On the first local run a random value is
// generated and saved in this project's user secrets, outside the repository; see the
// README for how to set your own.
var encryptionSecret = builder.AddParameter(
    "encryption-secret",
    new GenerateParameterDefault { MinLength = 48, Special = false },
    secret: true,
    persist: true);

// Both run as local containers: the Linux-based Cosmos DB emulator and Azurite.
var cosmos = builder.AddAzureCosmosDB("cosmos").RunAsEmulator();

// The database and its containers come from DataModelSchema, the same definitions the API
// uses. Aspire creates them in the emulator on start and emits them for Azure on publish.
var database = cosmos.AddCosmosDatabase(DataModelSchema.DatabaseName);
foreach (var definition in DataModelSchema.Containers)
{
    var container = database.AddContainer(definition.Name, definition.PartitionKeyPath);
    // Read when Aspire creates the container in the emulator.
    container.Resource.ContainerProperties.DefaultTimeToLive = definition.DefaultTimeToLiveSeconds;
}

// The generated Azure definitions carry only the partition key, so time-to-live is added here.
cosmos.ConfigureInfrastructure(infrastructure =>
{
    foreach (var container in infrastructure.GetProvisionableResources().OfType<CosmosDBSqlContainer>())
    {
        var definition = DataModelSchema.Containers.Single(c => c.Name == container.Name.Value);
        if (definition.DefaultTimeToLiveSeconds is { } seconds)
        {
            container.Resource.DefaultTtl = seconds;
        }
    }
});

var storage = builder.AddAzureStorage("storage").RunAsEmulator();
var blobs = storage.AddBlobs("blobs");
// The one container the API keeps uploaded files in. Aspire creates it in Azurite on
// start and emits it for Azure on publish; the API never creates containers itself.
var files = storage.AddBlobContainer(DataModelSchema.FilesBlobContainer);

builder.AddProject<Projects.FluentEscritor_Api>("api")
    .WithReference(cosmos).WaitFor(database)
    .WithReference(blobs).WaitFor(files)
    .WithEnvironment("App__EncryptionSecret", encryptionSecret)
    .WithHttpHealthCheck("/health");

builder.Build().Run();
