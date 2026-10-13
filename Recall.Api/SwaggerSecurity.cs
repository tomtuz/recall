using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Recall.Api;

/// <summary>
/// Per-operation OpenAPI security, derived from endpoint metadata:
/// [AllowAnonymous] → no credential (refresh: optional refresh cookie instead of a body token);
/// a documented 401 → Bearer required; otherwise Bearer optional (guests read public data).
/// </summary>
public sealed class SwaggerSecurityOperationFilter : IOperationFilter
{
    public const string Bearer = "Bearer";
    public const string RefreshCookie = "RefreshCookie";

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;
        if (metadata.OfType<IAllowAnonymous>().Any())
        {
            var bodyType = context.ApiDescription.ParameterDescriptions
                .FirstOrDefault(p => p.Source == Microsoft.AspNetCore.Mvc.ModelBinding.BindingSource.Body)?.Type;
            if (bodyType == typeof(RefreshRequest))
                operation.Security = [Requirement(RefreshCookie), new OpenApiSecurityRequirement()];
            return;
        }

        operation.Security = operation.Responses.ContainsKey("401")
            ? [Requirement(Bearer)]
            : [new OpenApiSecurityRequirement(), Requirement(Bearer)];
    }

    private static OpenApiSecurityRequirement Requirement(string scheme) => new()
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = scheme } }] = [],
    };
}
