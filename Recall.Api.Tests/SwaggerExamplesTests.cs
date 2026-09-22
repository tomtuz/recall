using System.Text.Json;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Recall.Api;
using Swashbuckle.AspNetCore.SwaggerGen;
using Xunit;

namespace Recall.Api.Tests;

public class SwaggerExamplesTests
{
    [Fact]
    public void Schema_filter_sets_typed_values_and_leaves_unrelated_schemas_unchanged()
    {
        var filter = new SwaggerExampleSchemaFilter();
        var schema = new OpenApiSchema();
        filter.Apply(schema, new SchemaFilterContext(typeof(CardCreateRequest), null!, new SchemaRepository()));
        var example = Assert.IsType<OpenApiObject>(schema.Example);
        Assert.Equal(2, Assert.IsType<OpenApiInteger>(example["difficulty"]).Value);
        Assert.Equal("201 Created", Assert.IsType<OpenApiString>(example["answer"]).Value);

        var original = new OpenApiString("keep this");
        foreach (var type in new[] { typeof(string), typeof(CourseDto), typeof(ErrorResponse) })
        {
            schema.Example = original;
            filter.Apply(schema, new SchemaFilterContext(type, null!, new SchemaRepository()));
            Assert.Same(original, schema.Example);
        }
    }

    [Fact]
    public void Examples_are_fresh_objects()
    {
        var first = Assert.IsType<OpenApiObject>(SwaggerExamples.For(typeof(ReviewRequest)));
        first["rating"] = new OpenApiString("invalid");
        var second = Assert.IsType<OpenApiObject>(SwaggerExamples.For(typeof(ReviewRequest)));
        Assert.Equal("good", Assert.IsType<OpenApiString>(second["rating"]).Value);
    }

    [Fact]
    public async Task Real_application_exports_request_and_response_examples_without_a_database()
    {
        var directory = Path.Combine(Path.GetTempPath(), "recall-openapi-" + Guid.NewGuid());
        try
        {
            var entry = typeof(Program).Assembly.EntryPoint!;
            var result = entry.Invoke(null, [new[] { "--export-openapi", directory }]);
            if (result is Task task) await task;
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "api-spec.json")));
            var root = json.RootElement;
            var schemas = root.GetProperty("components").GetProperty("schemas");
            var expected = new[] { "CourseCreateRequest", "CourseUpdateRequest", "TopicCreateRequest", "TopicUpdateRequest", "CardCreateRequest", "CardUpdateRequest", "CardImportRequest", "CardImportItem", "ReviewRequest", "RegisterRequest", "LoginRequest", "RefreshRequest" };
            foreach (var name in expected)
                Assert.Equal(JsonValueKind.Object, schemas.GetProperty(name).GetProperty("example").ValueKind);

            var paths = root.GetProperty("paths");
            Assert.Equal(16, paths.EnumerateObject().Count());
            var requests = 0;
            foreach (var path in paths.EnumerateObject())
            foreach (var operation in path.Value.EnumerateObject())
            {
                if (!operation.Value.TryGetProperty("requestBody", out var body)) continue;
                var media = body.GetProperty("content").GetProperty("application/json");
                var schemaName = media.GetProperty("schema").GetProperty("$ref").GetString()!.Split('/')[^1];
                var example = media.GetProperty("examples").GetProperty("request").GetProperty("value");
                Assert.True(JsonElement.DeepEquals(schemas.GetProperty(schemaName).GetProperty("example"), example));
                Assert.False(media.TryGetProperty("example", out _));
                requests++;
            }
            Assert.Equal(12, requests);
            var responses = paths.GetProperty("/api/v1/courses").GetProperty("post").GetProperty("responses");
            var created = responses.GetProperty("201").GetProperty("content").GetProperty("application/json").GetProperty("examples").GetProperty("created").GetProperty("value");
            Assert.Equal("WEB101", created.GetProperty("code").GetString());
            var error = responses.GetProperty("400").GetProperty("content").GetProperty("application/json").GetProperty("examples").GetProperty("missingTitle").GetProperty("value");
            Assert.Equal("VALIDATION_ERROR", error.GetProperty("errors")[0].GetProperty("code").GetString());
            Assert.False(responses.GetProperty("401").GetProperty("content").GetProperty("application/json").TryGetProperty("examples", out _));
            Assert.False(schemas.GetProperty("ErrorResponse").TryGetProperty("example", out _));
            var yaml = await File.ReadAllTextAsync(Path.Combine(directory, "api-spec.yaml"));
            Assert.Contains("examples:", yaml);
            Assert.Contains("missingTitle:", yaml);
            Assert.Contains("WEB101", yaml);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
