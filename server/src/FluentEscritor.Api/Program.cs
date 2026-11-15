using FluentEscritor.Api.Configuration;
using FluentEscritor.Api.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddAppOptions();
builder.AddStorage();

var app = builder.Build();

app.EnsureStorageConfigured();
app.MapDefaultEndpoints();

app.Run();
