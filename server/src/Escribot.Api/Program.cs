using Escribot.Api.Api;
using Escribot.Api.Auth;
using Escribot.Api.Configuration;
using Escribot.Api.Email;
using Escribot.Api.Hosting;
using Escribot.Api.Storage;

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
app.UseProxyHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}
app.UseSecurityHeaders();
app.UseClientAppFiles();
app.UseAuth();
app.MapDataApi();
app.MapDefaultEndpoints();
app.MapClientApp();

app.Run();
