using Azure.Provisioning.AppContainers;
using Azure.Provisioning.CosmosDB;
using Azure.Provisioning.KeyVault;
using Azure.Provisioning.Storage;
using Escribot.DataModel;

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
storage.ConfigureInfrastructure(infrastructure =>
{
    foreach (var account in infrastructure.GetProvisionableResources().OfType<StorageAccount>())
    {
        account.AllowBlobPublicAccess = false;
        account.Sku = new StorageSku { Name = StorageSkuName.StandardLrs };
    }
});
var blobs = storage.AddBlobs("blobs");
// The one container the API keeps uploaded files in. Aspire creates it in Azurite on
// start and emits it for Azure on publish; the API never creates containers itself.
var files = storage.AddBlobContainer(DataModelSchema.FilesBlobContainer);

var api = builder.AddProject<Projects.Escribot_Api>("api")
    .WithReference(cosmos).WaitFor(database)
    .WithReference(blobs).WaitFor(files)
    .WithHttpHealthCheck("/health");

// The browser app. Locally it runs on the Vite dev server, which reloads on every change
// and passes /api on to the API (see client/vite.config.ts), so the whole app is at the
// Vite address. When published it is not a resource of its own: the API image serves the
// built files (see server/Dockerfile). Tests of the API alone turn it off with
// BrowserApp:Enabled=false.
if (!builder.ExecutionContext.IsPublishMode && builder.Configuration["BrowserApp:Enabled"] != "false")
{
    var web = builder.AddViteApp("web", "../../../client")
        .WithNpm(install: true)
        .WithReference(api)
        .WaitFor(api)
        .WithExternalHttpEndpoints();

    // Links in emails and Stripe's return addresses point at the page people actually use.
    api.WithEnvironment("App__PublicUrl", web.GetEndpoint("http"));
}

// Secrets reach the API as environment variables either way. Locally they come straight from
// the parameters, as Key Vault has no emulator. When published to Azure each one is stored in
// this Key Vault, and the environment variable refers to the vault secret instead of holding
// the value, so the value never appears in the deployment templates. The API reads them with
// its managed identity, which may only read secrets.
var vault = builder.ExecutionContext.IsPublishMode ? builder.AddAzureKeyVault("vault") : null;
if (vault is not null)
{
    // The API runs in Azure Container Apps, which resolves the Key Vault references and
    // gives the app the managed identity the role below is assigned to.
    builder.AddAzureContainerAppEnvironment("aca");
    api.WithExternalHttpEndpoints();
    api.WithEnvironment("App__TrustForwardedHeaders", "true");
    api.WithRoleAssignments(vault, KeyVaultBuiltInRole.KeyVaultSecretsUser);
    api.PublishAsAzureContainerApp((_, app) =>
    {
        app.Template.Scale.MinReplicas = 0;
        app.Template.Scale.MaxReplicas = 3;

        var container = app.Template.Containers.Single().Value!;
        container.Probes.Add(new ContainerAppProbe
        {
            ProbeType = ContainerAppProbeType.Liveness,
            HttpGet = new ContainerAppHttpRequestInfo { Path = "/alive", Port = 8080 },
            InitialDelaySeconds = 5,
            PeriodSeconds = 30,
        });
        container.Probes.Add(new ContainerAppProbe
        {
            ProbeType = ContainerAppProbeType.Readiness,
            HttpGet = new ContainerAppHttpRequestInfo { Path = "/health", Port = 8080 },
            InitialDelaySeconds = 5,
            PeriodSeconds = 30,
        });
    });
}

void WithSecret(string variable, IResourceBuilder<ParameterResource> parameter)
{
    if (vault is null)
    {
        api.WithEnvironment(variable, parameter);
        return;
    }

    // The secret keeps the parameter's name in the vault, which is already a valid Key Vault
    // secret name (letters, digits and dashes). Its Aspire resource needs a name of its own.
    var secretName = parameter.Resource.Name;
    vault.AddSecret($"{vault.Resource.Name}-{secretName}", secretName, parameter);
    api.WithEnvironment(variable, vault.GetSecret(secretName));
}

WithSecret("App__EncryptionSecret", encryptionSecret);

// Billing stays off unless these are set, for example in this project's user secrets
// (`dotnet user-secrets set STRIPE_SECRET_KEY sk_test_...`). The secrets go through
// WithSecret; the price ids are not secret and are passed on as they are.
foreach (var (name, parameterName) in new[] { ("STRIPE_SECRET_KEY", "stripe-secret-key"), ("STRIPE_WEBHOOK_SECRET", "stripe-webhook-secret") })
{
    if (builder.Configuration[name] is { Length: > 0 })
    {
        WithSecret(name, builder.AddParameterFromConfiguration(parameterName, name, secret: true));
    }
}

foreach (var name in new[] { "STRIPE_PRICE_PRO_MONTHLY", "STRIPE_PRICE_PRO_YEARLY" })
{
    if (builder.Configuration[name] is { Length: > 0 } value)
    {
        api.WithEnvironment(name, value);
    }
}

builder.Build().Run();
