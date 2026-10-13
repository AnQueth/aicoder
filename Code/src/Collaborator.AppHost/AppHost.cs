using AzureKeyVaultEmulator.Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var apiKey = builder.AddParameter("api-key", secret: true);
var workflowKey = builder.AddParameter("workflow-key", secret: true);
var database = builder.AddPostgres("postgres")
    .WithImageTag("17")
    .WithDataVolume("collaborator-postgres-data-v2")
    .AddDatabase("collaborator");
// Local Key Vault emulator (Docker). Secret names use "--" which maps to ":" in .NET configuration.
var keyVault = builder.AddAzureKeyVault("keyvault")
    .RunAsEmulator();
// Aspire resource names can't contain "--", so the resource name differs from the vault secret name.
keyVault.AddSecret("security-apikey", "Security--ApiKey", apiKey);
keyVault.AddSecret("security-workflowkey", "Security--WorkflowKey", workflowKey);
var storage = builder.AddAzureStorage("storage")
    .RunAsEmulator(emulator => emulator.WithDataVolume("collaborator-azurite-data"));

var migrations = builder.AddProject<Projects.Collaborator_Migrator>("migrations")
    .WithReference(database)
    .WaitFor(database);
var mode = builder.Configuration["Agents:Mode"] ?? "Copilot";
var workflows = builder.AddAzureFunctionsProject<Projects.Collaborator_Workflows>("workflows")
    .WithHttpEndpoint(targetPort: 7119)
    .WithHostStorage(storage)
    .WithReference(database)
    .WithReference(keyVault)
    .WithAzureKeyVaultEmulatorCredentials(keyVault)
    .WithEnvironment("Agents__Mode", mode)
    .WaitFor(keyVault)
    .WaitForCompletion(migrations);
if (mode == "Copilot")
{
    workflows.WithEnvironment("Agents__Copilot__Model", builder.Configuration["Agents:Copilot:Model"]);
}
builder.AddProject<Projects.Collaborator_Api>("api")
    .WithReference(database)
    .WithReference(workflows)
    .WithReference(keyVault)
    .WithAzureKeyVaultEmulatorCredentials(keyVault)
    .WaitFor(keyVault)
    .WithHttpHealthCheck("/health")
    .WaitForCompletion(migrations)
    .WaitFor(workflows);

builder.Build().Run();
