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

var app = builder.Build();

app.EnsureStorageConfigured();
app.EnsureEmailConfigured();
app.UseAuth();
app.MapDefaultEndpoints();

app.Run();
