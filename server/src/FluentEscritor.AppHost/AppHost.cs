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
var blobs = builder.AddAzureStorage("storage").RunAsEmulator().AddBlobs("blobs");

builder.AddProject<Projects.FluentEscritor_Api>("api")
    .WithReference(cosmos).WaitFor(cosmos)
    .WithReference(blobs).WaitFor(blobs)
    .WithEnvironment("App__EncryptionSecret", encryptionSecret)
    .WithHttpHealthCheck("/health");

builder.Build().Run();
