using Elsa.Extensions;
using Elsa.Persistence.EFCore;
using Elsa.Persistence.EFCore.Extensions;
using Elsa.Persistence.EFCore.Modules.Identity;
using Elsa.Persistence.EFCore.Modules.Management;
using Elsa.Persistence.EFCore.Modules.Runtime;

var builder = WebApplication.CreateBuilder(args);

var connectionString =
    Environment.GetEnvironmentVariable("NHAMBS_CONNECTION")
    ?? builder.Configuration.GetConnectionString("Elsa")
    ?? "Host=localhost;Port=54322;Database=nhambs;Username=nhambs;Password=nhambs";

var schema = Environment.GetEnvironmentVariable("NHAMBS_ELSA_SCHEMA") ?? "elsa";
var isDevelopment = builder.Environment.IsDevelopment();

string DevelopmentDefault(string name, string value)
{
    var configured = Environment.GetEnvironmentVariable(name);

    if (!string.IsNullOrWhiteSpace(configured))
    {
        return configured;
    }

    if (isDevelopment)
    {
        return value;
    }

    throw new InvalidOperationException(
        $"{name} must be configured outside the Development environment."
    );
}

var adminUser = DevelopmentDefault("NHAMBS_ELSA_ADMIN_USER", "admin");
var adminPassword = DevelopmentDefault("NHAMBS_ELSA_ADMIN_PASSWORD", "nhambs-elsa-admin");
var signingKey = DevelopmentDefault(
    "NHAMBS_ELSA_SIGNING_KEY",
    "nhambs-local-elsa-signing-key-change-me-before-deploying"
);

var dbOptions = new ElsaDbContextOptions { SchemaName = schema };

builder.Services.AddElsa(elsa =>
{
    elsa.UseWorkflowManagement(management =>
        management.UseEntityFrameworkCore(ef =>
        {
            ef.UsePostgreSql(connectionString, dbOptions);
            ef.RunMigrations = true;
        })
    );

    elsa.UseWorkflowRuntime(runtime =>
        runtime.UseEntityFrameworkCore(ef =>
        {
            ef.UsePostgreSql(connectionString, dbOptions);
            ef.RunMigrations = true;
        })
    );

    elsa.UseIdentity(identity =>
    {
        identity.TokenOptions = options => options.SigningKey = signingKey;

        identity.UseEntityFrameworkCore(ef =>
        {
            ef.UsePostgreSql(connectionString, dbOptions);
            ef.RunMigrations = true;
        });

        identity.UseDefaultAdmin(adminUser, adminPassword, "admin", new List<string> { "*" });
    });

    elsa.UseDefaultAuthentication();
    elsa.UseWorkflowsApi();
    elsa.UseDashboardApi();
    elsa.UseWorkflowRuntimeDashboard();
    elsa.UseRealTimeWorkflows();
    elsa.UseScheduling();

    elsa.UseCSharp();
    elsa.UseJavaScript(options => options.AllowClrAccess = false);
    elsa.UseLiquid();

    NHAMBS.Workflows.Registration.register(elsa);
});

builder.Services.AddCors(cors =>
    cors.AddDefaultPolicy(policy =>
        policy
            .AllowAnyOrigin()
            .AllowAnyHeader()
            .AllowAnyMethod()
            .WithExposedHeaders("x-elsa-workflow-instance-id")
    )
);

builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseCors();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseWorkflowsApi();
app.UseWorkflowsSignalRHubs();

app.MapStaticAssets();
app.MapHealthChecks("/health");
app.MapFallbackToFile("index.html");

app.Run();
