using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using ShopifySharp.Credentials;
using ShopifySharp.Utilities;

namespace ShopifySharp.GraphQL.Parser.CLI;

public class GetSchemaCommand(CommandContext context, IServiceProvider serviceProvider)
{
    private readonly Regex _apiVersionRegex = new(@"^\d{4}-\d{2}$", RegexOptions.None, TimeSpan.FromMilliseconds(500));

    public async Task<int> ExecuteAsync(GetSchemaOptions options, CancellationToken cancellationToken = default)
    {
        if (!_apiVersionRegex.IsMatch(options.ApiVersion))
        {
            context.PrintErrors($"--api-version \"{options.ApiVersion}\" is invalid, it must follow the format \"2025-07\".");
            return 1;
        }

        var shopDomain = options.ShopDomain;
        var accessToken = options.AccessToken;

        // Grab the domain and token from the sops env file if CLI args are not provided
        if (string.IsNullOrEmpty(shopDomain) || string.IsNullOrEmpty(accessToken))
        {
            var sopsEnvFile = Environment.GetEnvironmentVariable("SOPS_ENV_FILE");

            if (string.IsNullOrEmpty(sopsEnvFile))
            {
                context.PrintErrors("Either --domain and --token must be provided, or SOPS_ENV_FILE must be set.");
                return 1;
            }

            (shopDomain, accessToken) = DecryptSopsEnvFile(sopsEnvFile);
        }

        if (string.IsNullOrEmpty(accessToken))
        {
            context.PrintErrors("--token cannot be empty.");
            return 1;
        }

        if (string.IsNullOrEmpty(shopDomain))
        {
            context.PrintErrors("--domain cannot be empty.");
            return 1;
        }

        if (string.IsNullOrEmpty(options.OutputFileName))
        {
            context.PrintErrors("--output cannot be empty.");
            return 1;
        }

        var resolvedOptions = options with { ShopDomain = shopDomain, AccessToken = accessToken };

        await DownloadSchemaFileFromShopifyApiAsync(resolvedOptions, cancellationToken);

        return 0;
    }

    private static (string? domain, string? accessToken) DecryptSopsEnvFile(string sopsEnvFile)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "sops",
            Arguments = $"decrypt {sopsEnvFile}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start sops process.");
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            var stderr = process.StandardError.ReadToEnd();
            throw new InvalidOperationException($"sops decrypt failed (exit code {process.ExitCode}): {stderr}");
        }

        var jsonBytes = process.StandardOutput.BaseStream;
        using var doc = JsonDocument.Parse(jsonBytes);
        var root = doc.RootElement;

        // Navigate nested JSON: ShopifySharp.Credentials.{field}
        string? TryNestedProperty(string field)
        {
            if (root.TryGetProperty("ShopifySharp", out var shopify) &&
                shopify.TryGetProperty("Credentials", out var creds) &&
                creds.TryGetProperty(field, out var value) &&
                value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
            return null;
        }

        var domain = TryNestedProperty("MyShopifyUrl");
        var token = TryNestedProperty("AccessToken");

        return (domain, token);
    }

    private async Task DownloadSchemaFileFromShopifyApiAsync(GetSchemaOptions options, CancellationToken cancellationToken = default)
    {
        var graphqlUtility = new ShopifyGraphqlUtility(serviceProvider);
        var credentials = new ShopifyApiCredentials(options.ShopDomain!, options.AccessToken!);
        var jsonSchema = await graphqlUtility.GetSchemaAsJsonStringAsync(credentials, options.ApiVersion, cancellationToken);

        try
        {
            await SaveSchemaContentsToPath(options.OutputFileName, jsonSchema, cancellationToken);
        }
        catch (ShopifyHttpException ex)
        {
            context.PrintErrors($"{ex.RequestInfo} {ex.RawBody}");
            throw;
        }
    }

    private static async Task SaveSchemaContentsToPath(string path, string schemaContents, CancellationToken cancellationToken = default)
    {
        var parentDir = Directory.GetParent(path);

        if (parentDir is { Exists: false })
            parentDir.Create();

        await File.WriteAllTextAsync(path, schemaContents, cancellationToken);
    }
}
