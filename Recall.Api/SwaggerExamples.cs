using System.Text.Json;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Recall.Api;

public static class SwaggerExamples
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Fictional documentation values: register first, then use your own login and refresh token.
    public static IOpenApiAny? For(Type type)
    {
        object? value = type switch
        {
            _ when type == typeof(CourseCreateRequest) => new CourseCreateRequest("WEB101", "Web application design", "REST APIs and browser clients", "Public"),
            _ when type == typeof(CourseUpdateRequest) => new CourseUpdateRequest("WEB101", "Web application design", "Updated course description", "Private"),
            _ when type == typeof(TopicCreateRequest) => new TopicCreateRequest("HTTP basics", "Methods and status codes", 1),
            _ when type == typeof(TopicUpdateRequest) => new TopicUpdateRequest("HTTP basics", "Requests and responses", 2),
            _ when type == typeof(CardCreateRequest) => new CardCreateRequest("Which status indicates a created resource?", "201 Created", "A successful POST", 2, "http,rest"),
            _ when type == typeof(CardUpdateRequest) => new CardUpdateRequest("Which HTTP status means Created?", "201", "A successful POST", 2, "http,rest"),
            _ when type == typeof(CardImportItem) => ImportItem(),
            _ when type == typeof(CardImportRequest) => new CardImportRequest([ImportItem()]),
            _ when type == typeof(ReviewRequest) => new ReviewRequest("good"),
            _ when type == typeof(RegisterRequest) => new RegisterRequest("learner@example.com", "Example-only-123!", "Example Learner"),
            _ when type == typeof(LoginRequest) => new LoginRequest("learner@example.com", "Example-only-123!"),
            _ when type == typeof(RefreshRequest) => new RefreshRequest("REPLACE_WITH_REFRESH_TOKEN_FROM_LOGIN"),
            _ when type == typeof(CourseDto) => new CourseDto(1, "WEB101", "Web application design", "REST APIs and browser clients", "Public", 1, "Example Learner", new DateTime(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc), new DateTime(2026, 9, 22, 12, 0, 0, DateTimeKind.Utc)),
            _ when type == typeof(ErrorResponse) => new ErrorResponse([new ErrorDetail("VALIDATION_ERROR", "Body: Title is required.")]),
            _ => null,
        };
        return value is null ? null : OpenApiAnyFactory.CreateFromJson(JsonSerializer.Serialize(value, JsonOptions));
    }

    private static CardImportItem ImportItem() => new("What does HTTP stand for?", "Hypertext Transfer Protocol", "A web protocol", 1, "http");
}

public sealed class SwaggerExampleSchemaFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.Type == typeof(CourseDto) || context.Type == typeof(ErrorResponse)) return;
        if (SwaggerExamples.For(context.Type) is { } example)
            schema.Example = example;
    }
}

public sealed class SwaggerExampleOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var bodyType = context.ApiDescription.ParameterDescriptions
            .FirstOrDefault(p => p.Source == Microsoft.AspNetCore.Mvc.ModelBinding.BindingSource.Body)?.Type;
        if (bodyType is not null && operation.RequestBody is not null && SwaggerExamples.For(bodyType) is { } request)
            Add(operation.RequestBody.Content, "request", request,
                bodyType == typeof(RefreshRequest) ? "Replace with the token returned by login; omit the body to use the HttpOnly cookie." :
                bodyType == typeof(LoginRequest) || bodyType == typeof(RegisterRequest) ? "Fictional account; register it first or substitute your own credentials." : "A populated request");

        foreach (var response in context.ApiDescription.SupportedResponseTypes)
        {
            if (response.StatusCode == 201 && response.Type == typeof(CourseDto) && operation.Responses.TryGetValue("201", out var created))
                Add(created.Content, "created", SwaggerExamples.For(typeof(CourseDto))!, "A newly created course");
            if (response.StatusCode == 400 && response.Type == typeof(ErrorResponse) && operation.Responses.TryGetValue("400", out var badRequest)
                && bodyType == typeof(CourseCreateRequest))
                Add(badRequest.Content, "missingTitle", SwaggerExamples.For(typeof(ErrorResponse))!, "The course title was omitted");
        }
    }

    private static void Add(IDictionary<string, OpenApiMediaType> content, string name, IOpenApiAny value, string summary)
    {
        foreach (var (mediaType, media) in content)
        {
            if (mediaType != "application/json") continue;
            media.Example = null;
            media.Examples[name] = new OpenApiExample { Summary = summary, Value = value };
        }
    }
}
