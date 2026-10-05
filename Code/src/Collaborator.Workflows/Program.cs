using Collaborator.Agents;
using Collaborator.Infrastructure;
using Collaborator.Workflows;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();
builder.AddServiceDefaults();
builder.AddKeyVaultSecrets();
builder.Services.AddOpenTelemetry().UseFunctionsWorkerDefaults();
builder.Services.AddPersistence(builder.Configuration);
builder.Services.AddPlanner(builder.Configuration);
builder.Services.AddScoped<RepositoryCatalogService>();
builder.Services.AddScoped<CodeChangeService>();
var workflowKey = builder.Configuration["Security:WorkflowKey"];
if (string.IsNullOrWhiteSpace(workflowKey) || workflowKey.Length < 32)
{
    throw new InvalidOperationException("Security:WorkflowKey of at least 32 characters is required.");
}
builder.Build().Run();
