using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;

namespace Microsoft.Extensions.Hosting;

public static class KeyVaultConfigurationExtensions
{
    // Loads Key Vault secrets into configuration when the AppHost provides a "keyvault" connection.
    // Secret names use "--" as the section separator, e.g. "Security--ApiKey" => "Security:ApiKey".
    public static TBuilder AddKeyVaultSecrets<TBuilder>(this TBuilder builder, string connectionName = "keyvault")
        where TBuilder : IHostApplicationBuilder
    {
        var vaultUri = builder.Configuration.GetConnectionString(connectionName);
        if (string.IsNullOrWhiteSpace(vaultUri))
        {
            return builder;
        }

        // Emulator compatibility: skip AAD instance discovery and challenge resource verification.
        var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions { DisableInstanceDiscovery = true });
        var client = new SecretClient(new Uri(vaultUri), credential, new SecretClientOptions { DisableChallengeResourceVerification = true });
        builder.Configuration.AddAzureKeyVault(client, new AzureKeyVaultConfigurationOptions());
        return builder;
    }
}
