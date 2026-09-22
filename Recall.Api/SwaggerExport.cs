using Microsoft.OpenApi;
using Microsoft.OpenApi.Extensions;
using Swashbuckle.AspNetCore.Swagger;

namespace Recall.Api;

public static class SwaggerExport
{
    public static async Task WriteAsync(WebApplication app, string directory)
    {
        // Starting initializes minimal API endpoint discovery; no database work runs in export mode.
        app.Urls.Clear();
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        try
        {
            var document = app.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "api-spec.json"), document.Serialize(OpenApiSpecVersion.OpenApi3_0, OpenApiFormat.Json));
            await File.WriteAllTextAsync(Path.Combine(directory, "api-spec.yaml"), document.Serialize(OpenApiSpecVersion.OpenApi3_0, OpenApiFormat.Yaml));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
