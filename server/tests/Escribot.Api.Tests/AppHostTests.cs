using System.Net;
using Aspire.Hosting.Testing;
using Escribot.Api.Data;
using Escribot.DataModel;
using Microsoft.Azure.Cosmos;

namespace Escribot.Api.Tests;

[Trait("Category", "Integration")]
[Collection(AppHostCollection.Name)]
public class AppHostTests(AppHostFixture fixture)
{
    [Fact]
    public async Task Api_is_healthy_with_both_emulators_attached()
    {
        // /health pings Cosmos DB and Blob Storage through the API's own storage interfaces.
        using var client = fixture.App.CreateHttpClient("api");
        using var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AppHost_creates_every_container_as_defined()
    {
        var database = fixture.Get<CosmosClient>().GetDatabase(DataModelSchema.DatabaseName);

        foreach (var definition in DataModelSchema.Containers)
        {
            ContainerProperties properties = await database.GetContainer(definition.Name).ReadContainerAsync();

            Assert.Equal(definition.PartitionKeyPath, properties.PartitionKeyPath);
            Assert.Equal(definition.DefaultTimeToLiveSeconds, properties.DefaultTimeToLive);
        }
    }

    [Fact]
    public void Api_seeds_the_curated_prompts_when_it_starts()
    {
        var shipped = CuratedPromptSeed.LoadEmbedded().ToDictionary(s => s.Prompt.Id, s => s.Hash);

        Assert.Equal(shipped.OrderBy(p => p.Key), fixture.CuratedPromptsSeededByApi.OrderBy(p => p.Key));
    }

    [Fact]
    public void Error_events_expire_after_30_days()
    {
        Assert.Equal(30 * 24 * 60 * 60, DataModelSchema.Errors.DefaultTimeToLiveSeconds);
    }
}
