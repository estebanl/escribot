using FluentEscritor.Api.Api;
using FluentEscritor.Api.Auth;
using FluentEscritor.Api.Configuration;
using FluentEscritor.Api.Email;
using FluentEscritor.Api.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddAppOptions();
builder.AddStorage();
builder.AddEmail();
builder.AddAuth();
builder.AddDataApi();

var app = builder.Build();

app.EnsureStorageConfigured();
app.EnsureEmailConfigured();
app.UseAuth();
app.MapDataApi();
app.MapDefaultEndpoints();

app.Run();
