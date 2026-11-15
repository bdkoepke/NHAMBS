using Elsa.Studio.Authentication.Abstractions.Models;
using Elsa.Studio.Authentication.ElsaIdentity.BlazorWasm.Extensions;
using Elsa.Studio.Authentication.ElsaIdentity.HttpMessageHandlers;
using Elsa.Studio.Authentication.ElsaIdentity.UI.Extensions;
using Elsa.Studio.Authentication.UI.Extensions;
using Elsa.Studio.Contracts;
using Elsa.Studio.Core.BlazorWasm.Extensions;
using Elsa.Studio.Dashboard.Extensions;
using Elsa.Studio.Extensions;
using Elsa.Studio.Localization.BlazorWasm.Extensions;
using Elsa.Studio.Localization.Models;
using Elsa.Studio.Models;
using Elsa.Studio.Shell;
using Elsa.Studio.Shell.Extensions;
using Elsa.Studio.Workflows.Designer.Extensions;
using Elsa.Studio.Workflows.Extensions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
var configuration = builder.Configuration;

builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");
builder.RootComponents.RegisterCustomElsaStudioElements();

// Elsa Studio authenticates against the Elsa server's identity endpoint.
var authProvider = configuration["Authentication:Provider"];

if (string.IsNullOrWhiteSpace(authProvider))
{
    authProvider = "ElsaIdentity";
}

if (!Enum.TryParse<StudioAuthenticationProvider>(authProvider, true, out var selectedAuthProvider))
{
    throw new InvalidOperationException($"Unsupported authentication provider: {authProvider}");
}

builder.Services.AddStudioAuthenticationMode(options => options.Provider = selectedAuthProvider);
builder.Services.AddElsaIdentity();
builder.Services.AddElsaIdentityUI();

var backendApiConfig = new BackendApiConfig
{
    ConfigureBackendOptions = options => configuration.GetSection("Backend").Bind(options),
    ConfigureHttpClientBuilder = options =>
        options.AuthenticationHandler = typeof(ElsaIdentityAuthenticatingApiHttpMessageHandler)
};

var localizationConfig = new LocalizationConfig
{
    ConfigureLocalizationOptions = options => configuration.GetSection("Localization").Bind(options)
};

builder.Services.AddCore();
builder.Services.AddShell();
builder.Services.AddAuthenticationUI();
builder.Services.AddRemoteBackend(backendApiConfig);
builder.Services.AddDashboardModule(backendApiConfig);
builder.Services.AddWorkflowsModule();
builder.Services.AddLocalizationModule(localizationConfig);

var app = builder.Build();

await app.UseElsaLocalization();

var startupTaskRunner = app.Services.GetRequiredService<IStartupTaskRunner>();
await startupTaskRunner.RunStartupTasksAsync();

await app.RunAsync();
