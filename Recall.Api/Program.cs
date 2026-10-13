using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Npgsql;
using Recall.Api;
using Recall.Api.Auth;
using Recall.Api.Services;

var exportOpenApi = args.Length == 2 && args[0] == "--export-openapi";
if (args.Contains("--export-openapi") && !exportOpenApi)
    throw new ArgumentException("Usage: --export-openapi <output-directory>");
var builder = WebApplication.CreateBuilder(exportOpenApi ? [] : args);
builder.Services.AddDbContext<RecallDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddSingleton<PasswordHasher>();

// JWT bearer authentication; Development without Jwt:Secret signs with a random per-run key.
var jwtSecret = (exportOpenApi ? Convert.ToBase64String(new byte[64]) : builder.Configuration["Jwt:Secret"])
    ?? (builder.Environment.IsDevelopment() ? Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64)) : throw new InvalidOperationException("Jwt:Secret is not configured."));
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "recall-api";
// SameSite=Lax is used for localhost and the planned same-origin Azure deployment.
var cookieSameSite = builder.Configuration.GetValue<bool>("Auth:CookieSameSiteNone")
    ? SameSiteMode.None
    : SameSiteMode.Lax;
var cookieSecure = builder.Configuration.GetValue<bool>("Auth:CookieSecure");
// Fail closed outside Development: an insecure refresh cookie would send the
// rotating token over plain HTTP (export mode never touches cookies).
if (!exportOpenApi && !builder.Environment.IsDevelopment() && !cookieSecure)
    throw new InvalidOperationException("Auth:CookieSecure must be enabled outside Development.");
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddPolicy("spa", policy =>
{
    if (allowedOrigins.Length > 0)
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    }
}));
builder.Services.AddSingleton(new JwtTokenService(jwtSecret, jwtIssuer));
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Keep raw claim names ("sub", "role") — no legacy URI mapping.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtIssuer,
            IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(jwtSecret)),
        };
    });
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(
    options => options.ThrowOnBadRequest = true);
builder.Services.AddAuthorization();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "Recall API", Version = "v1" });
    c.SchemaFilter<SwaggerExampleSchemaFilter>();
    c.OperationFilter<SwaggerExampleOperationFilter>();
    // JWT bearer scheme so Swagger UI can call protected endpoints.
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Access token from /api/v1/auth/login (paste without the 'Bearer ' prefix).",
    });
    c.AddSecurityDefinition(SwaggerSecurityOperationFilter.RefreshCookie, new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Cookie,
        Name = "recall_refresh",
        Description = "HttpOnly refresh-token cookie set by login/register/refresh (path /api/v1/auth).",
    });
    // Per-operation requirements: anonymous auth endpoints, required or optional bearer elsewhere.
    c.OperationFilter<SwaggerSecurityOperationFilter>();
});

if (exportOpenApi)
{
    builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
    // Export never uses persistent keys, including the key-ring warmup service.
    var keyWarmup = builder.Services.FirstOrDefault(service =>
        service.ServiceType == typeof(IHostedService) && service.ImplementationType?.Name == "DataProtectionHostedService");
    if (keyWarmup is not null) builder.Services.Remove(keyWarmup);
}

var app = builder.Build();

// Sanitized error for unexpected failures: mapped exceptions become 4xx,
// everything else 500 with no stack trace.
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, code, message) = exception switch
    {
        // Malformed request bodies / query values: client error, not a server fault.
        BadHttpRequestException bad => (bad.StatusCode, "BAD_REQUEST", "The request is invalid."),
        DbUpdateException { InnerException: PostgresException { SqlState: "23505" } } =>
            (StatusCodes.Status409Conflict, "CONFLICT", "A record with the same unique value already exists."),
        PostgresException { SqlState: "23505" } =>
            (StatusCodes.Status409Conflict, "CONFLICT", "A record with the same unique value already exists."),
        // A child row appeared between the parent-empty check and the delete.
        DbUpdateException { InnerException: PostgresException { SqlState: "23503" } } =>
            (StatusCodes.Status409Conflict, "CONFLICT", "Related resources changed. Reload and try again."),
        PostgresException { SqlState: "23503" } =>
            (StatusCodes.Status409Conflict, "CONFLICT", "Related resources changed. Reload and try again."),
        // The row was changed or deleted by someone else between load and save.
        DbUpdateConcurrencyException =>
            (StatusCodes.Status409Conflict, "CONFLICT", "The resource changed or was deleted. Reload and try again."),
        // Valid JSON containing NUL is rejected by PostgreSQL text columns.
        DbUpdateException { InnerException: PostgresException { SqlState: "22021" } } =>
            (StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Text contains a character the database cannot accept."),
        PostgresException { SqlState: "22021" } =>
            (StatusCodes.Status400BadRequest, "VALIDATION_ERROR", "Text contains a character the database cannot accept."),
        _ => (StatusCodes.Status500InternalServerError, "SERVER_ERROR", "An unexpected error occurred."),
    };
    context.Response.StatusCode = status;
    context.Response.ContentType = "application/json";
    await context.Response.WriteAsJsonAsync(new ErrorResponse([new ErrorDetail(code, message)]));
}));

app.UseCors("spa");
app.UseAuthentication();
app.UseAuthorization();

// RFC 9110: every 401 carries a WWW-Authenticate challenge. Handlers return the
// JSON error envelope themselves, so the header is added on the way out.
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        if (context.Response.StatusCode == StatusCodes.Status401Unauthorized &&
            !context.Response.Headers.ContainsKey("WWW-Authenticate"))
        {
            var presentedToken = context.Request.Headers.Authorization.ToString()
                .StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
            context.Response.Headers.WWWAuthenticate = presentedToken && context.User.UserId() is null
                ? "Bearer error=\"invalid_token\""
                : "Bearer";
        }
        return Task.CompletedTask;
    });
    await next();
});

// Auth-mutation origin gate: CORS restricts response *reading*, it cannot stop
// a simple cross-site POST from executing. Refresh rotation is a bodyless
// cookie POST, so reject browser-originated auth requests from untrusted
// origins. Originless clients (Postman, curl, the Python smoke suite), requests
// whose Origin matches the public Host, and explicitly allowed fallback SPA
// origins pass. A same-origin reverse proxy preserves the original Host when
// proxying to the API.
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    // Method + prefix match (case-insensitive, trailing-slash tolerant): route
    // matching accepts variants like /auth/REFRESH/ that a suffix check misses.
    if (HttpMethods.IsPost(context.Request.Method) &&
        path.StartsWithSegments("/api/v1/auth", StringComparison.OrdinalIgnoreCase))
    {
        var origin = context.Request.Headers.Origin.ToString();
        var forwardedProto = context.Request.Headers["X-Forwarded-Proto"].ToString();
        var publicScheme = forwardedProto.Length > 0
            ? forwardedProto.Split(',')[0].Trim()
            : context.Request.Scheme;
        var isSameOrigin = Uri.TryCreate(origin, UriKind.Absolute, out var originUri) &&
            string.Equals(originUri.Scheme, publicScheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(originUri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
        if (origin.Length > 0 && !isSameOrigin && !allowedOrigins.Contains(origin, StringComparer.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new ErrorResponse(
                [new ErrorDetail("FORBIDDEN_ORIGIN", "Cross-site request origin is not allowed.")]));
            return;
        }
    }
    await next();
});

// Apply migrations and seed demo data on startup (dev convenience).
// Demo accounts are only seeded in Development — never with a production DB.
if (!exportOpenApi)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<RecallDbContext>();
    db.Database.Migrate();
    if (app.Environment.IsDevelopment()) DbSeed.EnsureSeeded(db);
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

var api = app.MapGroup("/api/v1");

// ---- Courses ----

api.MapGet("/courses", async (RecallDbContext db, int? page, int? pageSize, string? search, string? visibility, string? sort, ClaimsPrincipal user) =>
{
    // Unknown filter or sort values are client errors, not a silent fallback.
    visibility = visibility?.ToLowerInvariant();
    if (visibility is not null and not ("public" or "private"))
        return ApiHelpers.ValidationError("visibility", "Visibility filter must be 'public' or 'private'.");
    if (sort is not null && !Validation.CourseSorts.Contains(sort))
        return ApiHelpers.ValidationError("sort", $"Sort must be one of: {string.Join(", ", Validation.CourseSorts)}.");

    var (p, size, offset) = ApiHelpers.ParsePaging(page, pageSize);
    var query = ApiHelpers.ScopeCourses(db, user);

    if (!string.IsNullOrWhiteSpace(search))
        query = query.Where(c => c.Title.ToLower().Contains(search.ToLower()) || c.Code.ToLower().Contains(search.ToLower()));
    if (visibility == "public")
        query = query.Where(c => c.Visibility == CourseVisibility.Public);
    // Apply the filter to the already-authorized query: guests get an empty
    // private list, contributors their own, admins all private courses.
    if (visibility == "private")
        query = query.Where(c => c.Visibility == CourseVisibility.Private);

    query = sort switch
    {
        "title" => query.OrderBy(c => c.Title).ThenBy(c => c.Id),
        "-title" => query.OrderByDescending(c => c.Title).ThenBy(c => c.Id),
        "code" => query.OrderBy(c => c.Code).ThenBy(c => c.Id),
        _ => query.OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Id),
    };

    var total = await query.CountAsync();
    var items = await query.Skip(offset).Take(size).ToListAsync();
    var dtoItems = items.Select(Mappers.ToDto).ToList();

    var queryParams = $"pageSize={size}";
    if (!string.IsNullOrWhiteSpace(search)) queryParams += $"&search={Uri.EscapeDataString(search)}";
    if (visibility is not null) queryParams += $"&visibility={visibility}";
    queryParams += sort is null ? "" : $"&sort={sort}";

    var links = new Dictionary<string, Link>
    {
        ["self"] = Links.Self($"/api/v1/courses?page={p}&{queryParams}"),
        ["first"] = Links.Self($"/api/v1/courses?page=1&{queryParams}"),
        ["create"] = Links.Post("/api/v1/courses"),
    };
    if ((long)p * size < total) links["next"] = Links.Self($"/api/v1/courses?page={p + 1}&{queryParams}");
    if (p > 1) links["prev"] = Links.Self($"/api/v1/courses?page={p - 1}&{queryParams}");

    return Results.Ok(new ListResponse<CourseDto>(dtoItems, links, ApiHelpers.BuildMeta(p, size, total)));
})
.Produces<ListResponse<CourseDto>>(StatusCodes.Status200OK)
.Produces<ErrorResponse>(StatusCodes.Status400BadRequest);

api.MapPost("/courses", async (RecallDbContext db, PasswordHasher hasher, ClaimsPrincipal user, CourseCreateRequest req) =>
{
    if (user.UserId() is null) return ApiHelpers.Unauthorized();

    var error = Validation.CheckTitle(req.Title, 200, "Title")
        ?? Validation.CheckTitle(req.Code, 30, "Code")
        ?? Validation.CheckOptionalText(req.Description, 2000, "Description");
    if (error != null) return ApiHelpers.ValidationError("Body", error);
    if (req.Visibility is not null)
    {
        var visibilityError = Validation.CheckVisibility(req.Visibility);
        if (visibilityError != null) return ApiHelpers.ValidationError("Body", visibilityError);
    }

    var course = new Course
    {
        Code = req.Code.Trim(),
        Title = req.Title.Trim(),
        Description = req.Description?.Trim() ?? "",
        Visibility = req.Visibility == "Public" ? CourseVisibility.Public : CourseVisibility.Private,
        OwnerId = user.UserId()!.Value,
        Owner = null!,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
    db.Courses.Add(course);
    await db.Entry(course).Reference(c => c.Owner).LoadAsync();
    await db.SaveChangesAsync();

    return Results.Created($"/api/v1/courses/{course.Id}", course.ToDto());
})
.Produces<CourseDto>(StatusCodes.Status201Created)
.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
.Produces<ErrorResponse>(StatusCodes.Status401Unauthorized)
.Produces<ErrorResponse>(StatusCodes.Status409Conflict);

api.MapGet("/courses/{id:int}", async (RecallDbContext db, ClaimsPrincipal user, int id) =>
{
    var course = await ApiHelpers.ScopeCourses(db, user).FirstOrDefaultAsync(c => c.Id == id);
    return course is null ? ApiHelpers.NotFound("Course", id) : Results.Ok(course.ToDto());
})
.Produces<CourseDto>(StatusCodes.Status200OK)
.Produces<ErrorResponse>(StatusCodes.Status404NotFound);

api.MapPatch("/courses/{id:int}", async (RecallDbContext db, ClaimsPrincipal user, int id, CourseUpdateRequest req) =>
{
    var userId = user.UserId();
    if (userId is null) return ApiHelpers.Unauthorized();

    var course = await db.Courses.Include(c => c.Owner).FirstOrDefaultAsync(c => c.Id == id);
    if (course is null) return ApiHelpers.NotFound("Course", id);
    if (course.OwnerId != userId && !user.IsAdmin()) return ApiHelpers.Forbidden();

    var patchError = Validation.CheckOptionalTitle(req.Title, 200, "Title")
        ?? Validation.CheckOptionalTitle(req.Code, 30, "Code")
        ?? Validation.CheckOptionalText(req.Description, 2000, "Description")
        ?? Validation.CheckVisibility(req.Visibility);
    if (patchError != null) return ApiHelpers.ValidationError("Body", patchError);

    if (req.Title is not null) course.Title = req.Title.Trim();
    if (req.Description is not null) course.Description = req.Description.Trim();
    if (req.Code is not null) course.Code = req.Code.Trim();
    if (req.Visibility is not null) course.Visibility = req.Visibility == "Public" ? CourseVisibility.Public : CourseVisibility.Private;
    course.UpdatedAt = DateTime.UtcNow;

    await db.SaveChangesAsync();
    return Results.Ok(course.ToDto());
})
.Produces<CourseDto>(StatusCodes.Status200OK)
.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
.Produces<ErrorResponse>(StatusCodes.Status401Unauthorized)
.Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
.Produces<ErrorResponse>(StatusCodes.Status404NotFound)
.Produces<ErrorResponse>(StatusCodes.Status409Conflict);

api.MapDelete("/courses/{id:int}", async (RecallDbContext db, ClaimsPrincipal user, int id) =>
{
    var userId = user.UserId();
    if (userId is null) return ApiHelpers.Unauthorized();

    var course = await db.Courses.AsNoTracking().Include(c => c.Topics).FirstOrDefaultAsync(c => c.Id == id);
    if (course is null) return ApiHelpers.NotFound("Course", id);
    if (course.OwnerId != userId && !user.IsAdmin()) return ApiHelpers.Forbidden();
    if (course.Topics.Count > 0) return ApiHelpers.Conflict("Course has topics and cannot be deleted. Delete the topics first.");

    db.Courses.Remove(course);
    await db.SaveChangesAsync();
    return Results.NoContent();
})
.Produces(StatusCodes.Status204NoContent)
.Produces<ErrorResponse>(StatusCodes.Status401Unauthorized)
.Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
.Produces<ErrorResponse>(StatusCodes.Status404NotFound)
.Produces<ErrorResponse>(StatusCodes.Status409Conflict);

// ---- Topics ----

api.MapGet("/courses/{courseId:int}/topics", async (RecallDbContext db, ClaimsPrincipal user, int courseId, int? page, int? pageSize) =>
{
    var course = await ApiHelpers.ScopeCourses(db, user).FirstOrDefaultAsync(c => c.Id == courseId);
    if (course is null) return ApiHelpers.NotFound("Course", courseId);

    var (p, size, offset) = ApiHelpers.ParsePaging(page, pageSize);
    var query = db.Topics.Include(t => t.Course).Where(t => t.CourseId == courseId).OrderBy(t => t.Position).ThenBy(t => t.Id);
    var total = await query.CountAsync();
    var items = await query.Skip(offset).Take(size).ToListAsync();

    var links = new Dictionary<string, Link>
    {
        ["self"] = Links.Self($"/api/v1/courses/{courseId}/topics?page={p}&pageSize={size}"),
        ["create"] = Links.Post($"/api/v1/courses/{courseId}/topics"),
    };
    if ((long)p * size < total) links["next"] = Links.Self($"/api/v1/courses/{courseId}/topics?page={p + 1}&pageSize={size}");
    if (p > 1) links["prev"] = Links.Self($"/api/v1/courses/{courseId}/topics?page={p - 1}&pageSize={size}");

    return Results.Ok(new ListResponse<TopicDto>(items.Select(Mappers.ToDto).ToList(), links, ApiHelpers.BuildMeta(p, size, total)));
})
.Produces<ListResponse<TopicDto>>(StatusCodes.Status200OK)
.Produces<ErrorResponse>(StatusCodes.Status404NotFound);

api.MapPost("/courses/{courseId:int}/topics", async (RecallDbContext db, ClaimsPrincipal user, int courseId, TopicCreateRequest req) =>
{
    var userId = user.UserId();
    if (userId is null) return ApiHelpers.Unauthorized();

    var course = await db.Courses.Include(c => c.Owner).FirstOrDefaultAsync(c => c.Id == courseId);
    if (course is null) return ApiHelpers.NotFound("Course", courseId);
    if (course.OwnerId != userId && !user.IsAdmin()) return ApiHelpers.Forbidden();

    var error = Validation.CheckTitle(req.Title, 200, "Title")
        ?? Validation.CheckOptionalText(req.Summary, 2000, "Summary");
    if (error != null) return ApiHelpers.ValidationError("Body", error);
    if (req.Position is < 1) return ApiHelpers.ValidationError("Body", "Position must be at least 1.");

    var maxPosition = await db.Topics.Where(t => t.CourseId == courseId).MaxAsync(t => (int?)t.Position) ?? 0;
    // The next automatic position would overflow int: the caller must choose one explicitly.
    if (req.Position is null && maxPosition == int.MaxValue)
        return ApiHelpers.Conflict("No automatic topic position is left in this course; set 'position' explicitly.");

    var topic = new Topic
    {
        CourseId = courseId,
        Title = req.Title.Trim(),
        Summary = req.Summary?.Trim() ?? "",
        Position = req.Position ?? maxPosition + 1,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
        Course = course,
    };
    db.Topics.Add(topic);
    await db.SaveChangesAsync();

    return Results.Created($"/api/v1/topics/{topic.Id}", topic.ToDto());
})
.Produces<TopicDto>(StatusCodes.Status201Created)
.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
.Produces<ErrorResponse>(StatusCodes.Status401Unauthorized)
.Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
.Produces<ErrorResponse>(StatusCodes.Status404NotFound)
.Produces<ErrorResponse>(StatusCodes.Status409Conflict);

api.MapGet("/topics/{id:int}", async (RecallDbContext db, ClaimsPrincipal user, int id) =>
{
    var topic = await db.Topics.Include(t => t.Course).ThenInclude(c => c!.Owner).FirstOrDefaultAsync(t => t.Id == id);
    if (topic is null) return ApiHelpers.NotFound("Topic", id);

    var courseVisible = topic.Course!.Visibility == CourseVisibility.Public;
    var userId = user.UserId();
    if (!courseVisible && userId != topic.Course.OwnerId && !user.IsAdmin()) return ApiHelpers.NotFound("Topic", id);

    return Results.Ok(topic.ToDto());
})
.Produces<TopicDto>(StatusCodes.Status200OK)
.Produces<ErrorResponse>(StatusCodes.Status404NotFound);

api.MapPatch("/topics/{id:int}", async (RecallDbContext db, ClaimsPrincipal user, int id, TopicUpdateRequest req) =>
{
    var userId = user.UserId();
    if (userId is null) return ApiHelpers.Unauthorized();

    var topic = await db.Topics.Include(t => t.Course).ThenInclude(c => c!.Owner).FirstOrDefaultAsync(t => t.Id == id);
    if (topic is null) return ApiHelpers.NotFound("Topic", id);
    if (topic.Course!.OwnerId != userId && !user.IsAdmin()) return ApiHelpers.Forbidden();

    var patchError = Validation.CheckOptionalTitle(req.Title, 200, "Title")
        ?? Validation.CheckOptionalText(req.Summary, 2000, "Summary")
        ?? (req.Position is < 1 ? "Position must be at least 1." : null);
    if (patchError != null) return ApiHelpers.ValidationError("Body", patchError);

    if (req.Title is not null) topic.Title = req.Title.Trim();
    if (req.Summary is not null) topic.Summary = req.Summary.Trim();
    if (req.Position is not null) topic.Position = req.Position.Value;
    topic.UpdatedAt = DateTime.UtcNow;

    await db.SaveChangesAsync();
    return Results.Ok(topic.ToDto());
})
.Produces<TopicDto>(StatusCodes.Status200OK)
.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
.Produces<ErrorResponse>(StatusCodes.Status401Unauthorized)
.Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
.Produces<ErrorResponse>(StatusCodes.Status404NotFound);

api.MapDelete("/topics/{id:int}", async (RecallDbContext db, ClaimsPrincipal user, int id) =>
{
    var userId = user.UserId();
    if (userId is null) return ApiHelpers.Unauthorized();

    var topic = await db.Topics.Include(t => t.Course).ThenInclude(c => c!.Owner).Include(t => t.Cards).FirstOrDefaultAsync(t => t.Id == id);
    if (topic is null) return ApiHelpers.NotFound("Topic", id);
    if (topic.Course!.OwnerId != userId && !user.IsAdmin()) return ApiHelpers.Forbidden();
    if (topic.Cards.Count > 0) return ApiHelpers.Conflict("Topic has cards and cannot be deleted. Delete the cards first.");

    db.Topics.Remove(topic);
    await db.SaveChangesAsync();
    return Results.NoContent();
})
.Produces(StatusCodes.Status204NoContent)
.Produces<ErrorResponse>(StatusCodes.Status401Unauthorized)
.Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
.Produces<ErrorResponse>(StatusCodes.Status404NotFound)
.Produces<ErrorResponse>(StatusCodes.Status409Conflict);

// ---- Cards ----

api.MapGet("/topics/{topicId:int}/cards", async (RecallDbContext db, ClaimsPrincipal user, int topicId, int? page, int? pageSize, string? state, string? tag, bool? due) =>
{
    var topic = await db.Topics.Include(t => t.Course).ThenInclude(c => c!.Owner).FirstOrDefaultAsync(t => t.Id == topicId);
    if (topic is null) return ApiHelpers.NotFound("Topic", topicId);

    var userId = user.UserId();
    if (topic.Course!.Visibility != CourseVisibility.Public && userId != topic.Course.OwnerId && !user.IsAdmin())
        return ApiHelpers.NotFound("Topic", topicId);

    var stateError = Validation.CheckCardState(state, out var parsedState);
    if (stateError != null) return ApiHelpers.ValidationError("state", stateError);

    var (p, size, offset) = ApiHelpers.ParsePaging(page, pageSize);
    var query = db.Cards.Include(c => c.Topic).Where(c => c.TopicId == topicId);

    if (parsedState is { } stateFilter)
        query = query.Where(c => c.State == stateFilter);
    if (!string.IsNullOrWhiteSpace(tag))
        query = query.Where(c => c.Tags.ToLower().Contains(tag.ToLower()));
    if (due == true)
        query = query.Where(c => c.DueAt != null && c.DueAt <= DateTime.UtcNow);
    else if (due == false)
        query = query.Where(c => c.DueAt == null || c.DueAt > DateTime.UtcNow);

    query = query.OrderBy(c => c.Id);
    var total = await query.CountAsync();
    var items = await query.Skip(offset).Take(size).ToListAsync();

    // Preserve active filters in pagination links so "next" never drops them.
    var cardFilters = "";
    if (state is not null) cardFilters += $"&state={Uri.EscapeDataString(state)}";
    if (!string.IsNullOrWhiteSpace(tag)) cardFilters += $"&tag={Uri.EscapeDataString(tag)}";
    if (due is not null) cardFilters += $"&due={due.Value.ToString().ToLowerInvariant()}";

    var links = new Dictionary<string, Link>
    {
        ["self"] = Links.Self($"/api/v1/topics/{topicId}/cards?page={p}&pageSize={size}{cardFilters}"),
        ["create"] = Links.Post($"/api/v1/topics/{topicId}/cards"),
    };
    if ((long)p * size < total) links["next"] = Links.Self($"/api/v1/topics/{topicId}/cards?page={p + 1}&pageSize={size}{cardFilters}");
    if (p > 1) links["prev"] = Links.Self($"/api/v1/topics/{topicId}/cards?page={p - 1}&pageSize={size}{cardFilters}");

    return Results.Ok(new ListResponse<CardDto>(items.Select(Mappers.ToDto).ToList(), links, ApiHelpers.BuildMeta(p, size, total)));
})
.Produces<ListResponse<CardDto>>(StatusCodes.Status200OK)
.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
.Produces<ErrorResponse>(StatusCodes.Status404NotFound);

api.MapPost("/topics/{topicId:int}/cards", async (RecallDbContext db, ClaimsPrincipal user, int topicId, CardCreateRequest req) =>
{
    var userId = user.UserId();
    if (userId is null) return ApiHelpers.Unauthorized();

    var topic = await db.Topics.Include(t => t.Course).ThenInclude(c => c!.Owner).FirstOrDefaultAsync(t => t.Id == topicId);
    if (topic is null) return ApiHelpers.NotFound("Topic", topicId);
    if (topic.Course!.OwnerId != userId && !user.IsAdmin()) return ApiHelpers.Forbidden();

    var error = Validation.CheckTitle(req.Question, 500, "Question")
        ?? Validation.CheckTitle(req.Answer, 1000, "Answer")
        ?? Validation.CheckDifficulty(req.Difficulty)
        ?? Validation.CheckOptionalText(req.Hint, 500, "Hint")
        ?? Validation.CheckOptionalText(req.Tags, 200, "Tags");
    if (error != null) return ApiHelpers.ValidationError("Body", error);

    var card = new Card
    {
        TopicId = topicId,
        Question = req.Question.Trim(),
        Answer = req.Answer.Trim(),
        Hint = req.Hint?.Trim(),
        Difficulty = req.Difficulty ?? 3,
        Tags = req.Tags?.Trim() ?? "",
        Topic = topic,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
    db.Cards.Add(card);
    await db.SaveChangesAsync();

    return Results.Created($"/api/v1/cards/{card.Id}", card.ToDto());
})
.Produces<CardDto>(StatusCodes.Status201Created)
.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
.Produces<ErrorResponse>(StatusCodes.Status401Unauthorized)
.Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
.Produces<ErrorResponse>(StatusCodes.Status404NotFound)
.Produces<ErrorResponse>(StatusCodes.Status409Conflict);

api.MapGet("/cards/{id:int}", async (RecallDbContext db, ClaimsPrincipal user, int id) =>
{
    var card = await db.Cards.Include(c => c.Topic).ThenInclude(t => t!.Course).ThenInclude(c => c!.Owner).FirstOrDefaultAsync(c => c.Id == id);
    if (card is null) return ApiHelpers.NotFound("Card", id);

    var course = card.Topic!.Course!;
    var userId = user.UserId();
    if (course.Visibility != CourseVisibility.Public && userId != course.OwnerId && !user.IsAdmin())
        return ApiHelpers.NotFound("Card", id);

    return Results.Ok(card.ToDto());
})
.Produces<CardDto>(StatusCodes.Status200OK)
.Produces<ErrorResponse>(StatusCodes.Status404NotFound);

api.MapPatch("/cards/{id:int}", async (RecallDbContext db, ClaimsPrincipal user, int id, CardUpdateRequest req) =>
{
    var userId = user.UserId();
    if (userId is null) return ApiHelpers.Unauthorized();

    var card = await db.Cards.Include(c => c.Topic).ThenInclude(t => t!.Course).ThenInclude(c => c!.Owner).FirstOrDefaultAsync(c => c.Id == id);
    if (card is null) return ApiHelpers.NotFound("Card", id);
    if (card.Topic!.Course!.OwnerId != userId && !user.IsAdmin()) return ApiHelpers.Forbidden();

    var diffError = Validation.CheckDifficulty(req.Difficulty);
    if (diffError != null) return ApiHelpers.ValidationError("Difficulty", diffError);
    var patchError = Validation.CheckOptionalTitle(req.Question, 500, "Question")
        ?? Validation.CheckOptionalTitle(req.Answer, 1000, "Answer")
        ?? Validation.CheckOptionalText(req.Hint, 500, "Hint")
        ?? Validation.CheckOptionalText(req.Tags, 200, "Tags");
    if (patchError != null) return ApiHelpers.ValidationError("Body", patchError);

    if (req.Question is not null) card.Question = req.Question.Trim();
    if (req.Answer is not null) card.Answer = req.Answer.Trim();
    if (req.Hint is not null) card.Hint = req.Hint.Trim() == "" ? null : req.Hint.Trim();
    if (req.Tags is not null) card.Tags = req.Tags.Trim();
    if (req.Difficulty is not null) card.Difficulty = req.Difficulty.Value;
    card.UpdatedAt = DateTime.UtcNow;

    await db.SaveChangesAsync();
    return Results.Ok(card.ToDto());
})
.Produces<CardDto>(StatusCodes.Status200OK)
.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
.Produces<ErrorResponse>(StatusCodes.Status401Unauthorized)
.Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
.Produces<ErrorResponse>(StatusCodes.Status404NotFound)
.Produces<ErrorResponse>(StatusCodes.Status409Conflict);

api.MapDelete("/cards/{id:int}", async (RecallDbContext db, ClaimsPrincipal user, int id) =>
{
    var userId = user.UserId();
    if (userId is null) return ApiHelpers.Unauthorized();

    var card = await db.Cards.Include(c => c.Topic).ThenInclude(t => t!.Course).ThenInclude(c => c!.Owner).FirstOrDefaultAsync(c => c.Id == id);
    if (card is null) return ApiHelpers.NotFound("Card", id);
    if (card.Topic!.Course!.OwnerId != userId && !user.IsAdmin()) return ApiHelpers.Forbidden();

    db.Cards.Remove(card);
    await db.SaveChangesAsync();
    return Results.NoContent();
})
.Produces(StatusCodes.Status204NoContent)
.Produces<ErrorResponse>(StatusCodes.Status401Unauthorized)
.Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
.Produces<ErrorResponse>(StatusCodes.Status404NotFound)
.Produces<ErrorResponse>(StatusCodes.Status409Conflict);

// ---- Hierarchical endpoint (spans all 3 objects) ----

api.MapGet("/courses/{courseId:int}/topics/{topicId:int}/cards", async (RecallDbContext db, ClaimsPrincipal user, int courseId, int topicId, int? page, int? pageSize) =>
{
    // Scope the course first so private courses are indistinguishable from missing ones.
    var course = await ApiHelpers.ScopeCourses(db, user).FirstOrDefaultAsync(c => c.Id == courseId);
    if (course is null) return ApiHelpers.NotFound("Course", courseId);

    var topic = await db.Topics.Include(t => t.Course).ThenInclude(c => c!.Owner).FirstOrDefaultAsync(t => t.Id == topicId && t.CourseId == courseId);
    if (topic is null) return ApiHelpers.NotFound("Topic", topicId);

    var (p, size, offset) = ApiHelpers.ParsePaging(page, pageSize);
    var query = db.Cards.Include(c => c.Topic).Where(c => c.TopicId == topicId).OrderBy(c => c.Id);
    var total = await query.CountAsync();
    var items = await query.Skip(offset).Take(size).ToListAsync();

    var hierarchicalPath = $"/api/v1/courses/{courseId}/topics/{topicId}/cards";
    var links = new Dictionary<string, Link>
    {
        ["self"] = Links.Self($"{hierarchicalPath}?page={p}&pageSize={size}"),
        ["first"] = Links.Self($"{hierarchicalPath}?page=1&pageSize={size}"),
    };
    if ((long)p * size < total) links["next"] = Links.Self($"{hierarchicalPath}?page={p + 1}&pageSize={size}");
    if (p > 1) links["prev"] = Links.Self($"{hierarchicalPath}?page={p - 1}&pageSize={size}");

    return Results.Ok(new ListResponse<CardDto>(items.Select(Mappers.ToDto).ToList(), links, ApiHelpers.BuildMeta(p, size, total)));
})
.Produces<ListResponse<CardDto>>(StatusCodes.Status200OK)
.Produces<ErrorResponse>(StatusCodes.Status404NotFound);

// ---- Composite reads ----

api.MapGet("/courses/{id:int}/overview", async (RecallDbContext db, ClaimsPrincipal user, int id) =>
{
    var course = await ApiHelpers.ScopeCourses(db, user).FirstOrDefaultAsync(c => c.Id == id);
    if (course is null) return ApiHelpers.NotFound("Course", id);

    var topics = await db.Topics.Where(t => t.CourseId == id).OrderBy(t => t.Position).ToListAsync();
    var topicIds = topics.Select(t => t.Id).ToList();

    // Aggregates computed in SQL instead of loading every card row.
    var cardsAgg = await db.Cards
        .Where(c => topicIds.Contains(c.TopicId))
        .GroupBy(_ => 1)
        .Select(g => new
        {
            Count = g.Count(),
            Due = g.Count(c => c.DueAt != null && c.DueAt <= DateTime.UtcNow),
            Average = g.Average(c => (double?)c.Difficulty) ?? 0,
            Hardest = g.Max(c => (int?)c.Difficulty) ?? 0,
        })
        .FirstOrDefaultAsync();
    var perTopic = await db.Cards
        .Where(c => topicIds.Contains(c.TopicId))
        .GroupBy(c => c.TopicId)
        .Select(g => new { TopicId = g.Key, Count = g.Count(), Due = g.Count(c => c.DueAt != null && c.DueAt <= DateTime.UtcNow) })
        .ToDictionaryAsync(x => x.TopicId, x => x);
    var perState = await db.Cards
        .Where(c => topicIds.Contains(c.TopicId))
        .GroupBy(c => c.State)
        .Select(g => new { State = g.Key, Count = g.Count() })
        .ToDictionaryAsync(x => x.State.ToString(), x => x.Count);

    var overview = new CourseOverviewDto(
        course.ToDto(),
        topics.Count,
        cardsAgg?.Count ?? 0,
        cardsAgg?.Due ?? 0,
        perState,
        new DifficultyMixDto(
            cardsAgg is null ? 0 : Math.Round(cardsAgg.Average, 2),
            cardsAgg?.Hardest ?? 0),
        topics.Select(t =>
        {
            perTopic.TryGetValue(t.Id, out var agg);
            return new TopicOverviewDto(
                t.Id, t.Title, t.Position,
                agg?.Count ?? 0, agg?.Due ?? 0);
        }).ToList());

    return Results.Ok(overview);
})
.Produces<CourseOverviewDto>(StatusCodes.Status200OK)
.Produces<ErrorResponse>(StatusCodes.Status404NotFound);

api.MapGet("/me/dashboard", async (RecallDbContext db, ClaimsPrincipal user) =>
{
    var userId = user.UserId();
    if (userId is null) return ApiHelpers.Unauthorized();

    var ownCourseIds = await db.Courses.Where(c => c.OwnerId == userId).Select(c => c.Id).ToListAsync();
    var ownTopicIds = await db.Topics.Where(t => ownCourseIds.Contains(t.CourseId)).Select(t => t.Id).ToListAsync();
    var cards = await db.Cards.Where(c => ownTopicIds.Contains(c.TopicId)).ToListAsync();
    var now = DateTime.UtcNow;

    var dashboard = new DashboardDto(
        cards.Count(c => c.DueAt != null && c.DueAt <= now),
        cards.Count(c => c.State == CardState.New),
        cards.Count(c => c.State == CardState.Learning || c.State == CardState.Relearning),
        cards.Count(c => c.State == CardState.Review),
        ownCourseIds.Count,
        cards.GroupBy(c => c.State.ToString()).ToDictionary(g => g.Key, g => g.Count()));

    return Results.Ok(dashboard);
})
.Produces<DashboardDto>(StatusCodes.Status200OK)
.Produces<ErrorResponse>(StatusCodes.Status401Unauthorized);

// ---- Review feature ----

api.MapGet("/review/queue", async (RecallDbContext db, ClaimsPrincipal user, int? limit) =>
{
    var userId = user.UserId();
    if (userId is null) return ApiHelpers.Unauthorized();

    var size = Math.Clamp(limit ?? 20, 1, 100);
    var ownCourseIds = await db.Courses.Where(c => c.OwnerId == userId).Select(c => c.Id).ToListAsync();
    var ownTopicIds = await db.Topics.Where(t => ownCourseIds.Contains(t.CourseId)).Select(t => t.Id).ToListAsync();
    var now = DateTime.UtcNow;

    var dueCards = await db.Cards
        .Include(c => c.Topic)
        .Where(c => ownTopicIds.Contains(c.TopicId) && (c.DueAt == null || c.DueAt <= now))
        // New cards (no due date) first, then oldest due; stable tie-break.
        .OrderBy(c => c.DueAt != null)
        .ThenBy(c => c.DueAt)
        .ThenBy(c => c.Id)
        .Take(size)
        .ToListAsync();

    var dtoCards = dueCards.Select(c => new ReviewQueueItemDto(
        c.ToDto(),
        new Dictionary<string, Link>
        {
            ["review"] = Links.Post($"/api/v1/cards/{c.Id}/review"),
            ["self"] = Links.Self($"/api/v1/cards/{c.Id}"),
        })).ToList();

    return Results.Ok(new ReviewQueueDto(dtoCards, dueCards.Count));
})
.Produces<ReviewQueueDto>(StatusCodes.Status200OK)
.Produces<ErrorResponse>(StatusCodes.Status401Unauthorized);

api.MapPost("/cards/{id:int}/review", async (RecallDbContext db, ClaimsPrincipal user, int id, ReviewRequest req) =>
{
    var userId = user.UserId();
    if (userId is null) return ApiHelpers.Unauthorized();

    // Lock the card row first, then read it: concurrent reviews of one card run
    // one after another, each scheduling from the state the previous one saved.
    await using var reviewTx = await db.Database.BeginTransactionAsync();
    await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Cards\" WHERE \"Id\" = {id} FOR UPDATE");

    var card = await db.Cards.Include(c => c.Topic).ThenInclude(t => t!.Course).ThenInclude(c => c!.Owner).FirstOrDefaultAsync(c => c.Id == id);
    if (card is null) return ApiHelpers.NotFound("Card", id);
    if (card.Topic!.Course!.OwnerId != userId && !user.IsAdmin()) return ApiHelpers.Forbidden();

    var validRatings = new[] { "again", "hard", "good", "easy" };
    if (!validRatings.Contains(req.Rating))
        return ApiHelpers.ValidationError("Rating", "Rating must be one of: again, hard, good, easy.");

    var now = DateTime.UtcNow;
    var (state, ease, interval, dueAt, reps, lapses) = Sm2Lite.Schedule(card, req.Rating, now);

    card.State = state;
    card.Ease = ease;
    card.IntervalDays = interval;
    card.DueAt = dueAt;
    card.Reps = reps;
    card.Lapses = lapses;
    card.LastReviewedAt = now;
    card.UpdatedAt = now;

    await db.SaveChangesAsync();
    await reviewTx.CommitAsync();

    return Results.Ok(new CardReviewDto(
        card.ToDto(), req.Rating, dueAt, interval));
})
.Produces<CardReviewDto>(StatusCodes.Status200OK)
.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
.Produces<ErrorResponse>(StatusCodes.Status401Unauthorized)
.Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
.Produces<ErrorResponse>(StatusCodes.Status404NotFound)
.Produces<ErrorResponse>(StatusCodes.Status409Conflict);

// ---- Bulk import (cards) ----

api.MapPost("/topics/{id:int}/cards/import", async (RecallDbContext db, ClaimsPrincipal user, int id, CardImportRequest req) =>
{
    var userId = user.UserId();
    if (userId is null) return ApiHelpers.Unauthorized();

    var topic = await db.Topics.Include(t => t.Course).ThenInclude(c => c!.Owner).FirstOrDefaultAsync(t => t.Id == id);
    if (topic is null) return ApiHelpers.NotFound("Topic", id);
    if (topic.Course!.OwnerId != userId && !user.IsAdmin()) return ApiHelpers.Forbidden();

    if (req.Cards is null || req.Cards.Count == 0)
        return ApiHelpers.ValidationError("Cards", "At least one card is required.");
    if (req.Cards.Count > Validation.MaxImportCards)
        return ApiHelpers.ValidationError("Cards", $"At most {Validation.MaxImportCards} cards can be imported at once.");

    var errors = new List<ErrorDetail>();
    var valid = new List<Card>();
    for (var i = 0; i < req.Cards.Count; i++)
    {
        var row = req.Cards[i];
        if (row is null)
        {
            errors.Add(new ErrorDetail("ROW_INVALID", $"Row {i + 1}: card object is required."));
            continue;
        }
        var questionError = Validation.CheckTitle(row.Question, 500, "Question");
        var answerError = Validation.CheckTitle(row.Answer, 1000, "Answer");
        var difficultyError = Validation.CheckDifficulty(row.Difficulty);
        var firstRowError = questionError ?? answerError ?? difficultyError
            ?? Validation.CheckOptionalText(row.Hint, 500, "Hint")
            ?? Validation.CheckOptionalText(row.Tags, 200, "Tags");
        if (firstRowError != null)
        {
            errors.Add(new ErrorDetail("ROW_INVALID", $"Row {i + 1}: {firstRowError}"));
            continue;
        }
        valid.Add(new Card
        {
            TopicId = id,
            Question = row.Question.Trim(),
            Answer = row.Answer.Trim(),
            Hint = row.Hint?.Trim(),
            Difficulty = row.Difficulty ?? 3,
            Tags = row.Tags?.Trim() ?? "",
            Topic = topic,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
    }

    if (errors.Count > 0)
        return Results.Json(new ErrorResponse(errors), statusCode: StatusCodes.Status400BadRequest);

    db.Cards.AddRange(valid);
    await db.SaveChangesAsync();

    return Results.Created(
        $"/api/v1/topics/{id}/cards",
        new CardImportDto(valid.Count, valid.Select(Mappers.ToDto).ToList()));
})
.Produces<CardImportDto>(StatusCodes.Status201Created)
.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
.Produces<ErrorResponse>(StatusCodes.Status401Unauthorized)
.Produces<ErrorResponse>(StatusCodes.Status403Forbidden)
.Produces<ErrorResponse>(StatusCodes.Status404NotFound)
.Produces<ErrorResponse>(StatusCodes.Status409Conflict);

// ---- Auth ----

api.MapPost("/auth/register", async (RecallDbContext db, PasswordHasher hasher, JwtTokenService jwt, HttpContext http, RegisterRequest req) =>
{
    var emailError = Validation.CheckTitle(req.Email, 200, "Email");
    if (emailError is null && !Validation.IsValidEmail(req.Email))
        emailError = "Email address format is invalid.";
    var passwordError = string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 8
        ? "Password must be at least 8 characters."
        : req.Password.Length > Validation.MaxPasswordLength
            ? $"Password must be at most {Validation.MaxPasswordLength} characters."
            : null;
    var nameError = Validation.CheckTitle(req.DisplayName, 100, "DisplayName");
    var firstError = emailError ?? passwordError ?? nameError;
    if (firstError != null) return ApiHelpers.ValidationError("Body", firstError);

    var normalizedEmail = req.Email.Trim().ToLowerInvariant();
    if (await db.Users.AnyAsync(u => u.Email == normalizedEmail))
        return ApiHelpers.Conflict($"A user with email '{normalizedEmail}' already exists.");

    var user = new AppUser
    {
        Email = normalizedEmail,
        DisplayName = req.DisplayName.Trim(),
        Role = "Contributor",
        PasswordHash = hasher.Hash(req.Password),
        CreatedAt = DateTime.UtcNow,
    };
    db.Users.Add(user);
    try
    {
        await db.SaveChangesAsync();
    }
    catch (DbUpdateException ex) when (db.Entry(user).State == EntityState.Added && IsUniqueViolation(ex))
    {
        // Concurrent registration on the same email: the unique index wins.
        return ApiHelpers.Conflict($"A user with email '{normalizedEmail}' already exists.");
    }

    var refreshToken = await IssueRefreshTokenAsync(db, jwt, user);
    SetRefreshCookie(http, refreshToken, jwt);
    // The raw refresh token stays out of JSON (HttpOnly cookie only) so an XSS
    // script cannot steal it; the body returns it only for non-browser tools.
    var response = Results.Created($"/api/v1/me/dashboard", new AuthResponse(
        jwt.CreateAccessToken(user),
        http.Request.Headers.ContainsKey("Origin") ? "" : refreshToken,
        new UserDto(user.Id, user.Email, user.DisplayName, user.Role)));
    return response;
})
.AllowAnonymous()
.Produces<AuthResponse>(StatusCodes.Status201Created)
.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
.Produces<ErrorResponse>(StatusCodes.Status409Conflict);

api.MapPost("/auth/login", async (RecallDbContext db, PasswordHasher hasher, JwtTokenService jwt, HttpContext http, LoginRequest req) =>
{
    var emailError = Validation.CheckTitle(req.Email, 200, "Email");
    if (emailError != null) return ApiHelpers.ValidationError("Body", emailError);
    // Same cap as registration: no stored password is longer, so skip hashing oversized input.
    if (req.Password?.Length > Validation.MaxPasswordLength)
        return ApiHelpers.ValidationError("Body", $"Password must be at most {Validation.MaxPasswordLength} characters.");

    var normalizedEmail = req.Email.Trim().ToLowerInvariant();
    var user = await db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail);
    if (user is null || !hasher.Verify(req.Password ?? "", user.PasswordHash))
        return Results.Json(
            new ErrorResponse([new ErrorDetail("INVALID_CREDENTIALS", "Email or password is incorrect.")]),
            statusCode: StatusCodes.Status401Unauthorized);

    var refreshToken = await IssueRefreshTokenAsync(db, jwt, user);
    SetRefreshCookie(http, refreshToken, jwt);
    // Same rule as register: browsers get the refresh token only via cookie.
    return Results.Ok(new AuthResponse(
        jwt.CreateAccessToken(user),
        http.Request.Headers.ContainsKey("Origin") ? "" : refreshToken,
        new UserDto(user.Id, user.Email, user.DisplayName, user.Role)));
})
.AllowAnonymous()
.Produces<AuthResponse>(StatusCodes.Status200OK)
.Produces<ErrorResponse>(StatusCodes.Status400BadRequest)
.Produces<ErrorResponse>(StatusCodes.Status401Unauthorized);

api.MapPost("/auth/refresh", async (RecallDbContext db, JwtTokenService jwt, HttpContext http, RefreshRequest? req) =>
{
    // The SPA sends the refresh token in the HttpOnly cookie; Postman sends it in the body.
    var presented = req?.RefreshToken ?? http.Request.Cookies["recall_refresh"];
    if (!JwtTokenService.TryHashRefreshToken(presented, out var tokenHash))
        return Results.Json(
            new ErrorResponse([new ErrorDetail("INVALID_REFRESH_TOKEN", "Refresh token is not valid.")]),
            statusCode: StatusCodes.Status401Unauthorized);

    var stored = await db.RefreshTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.TokenHash == tokenHash);

    if (stored is null)
        return Results.Json(
            new ErrorResponse([new ErrorDetail("INVALID_REFRESH_TOKEN", "Refresh token is not valid.")]),
            statusCode: StatusCodes.Status401Unauthorized);

    // Serialize refresh/replay/logout for this user on the Users row (SELECT
    // ... FOR UPDATE). All token rows of a family share the user, so a row
    // lock taken here makes concurrent rotation, replay-revocation, and
    // logout family-wide updates run one at a time — no successor can escape
    // a revocation that races it.
    await using var familyTx = await db.Database.BeginTransactionAsync();
    await db.Users
        .FromSqlInterpolated($"SELECT * FROM \"Users\" WHERE \"Id\" = {stored.User.Id} FOR UPDATE")
        .AsNoTracking()
        .LoadAsync();

    if (stored.RevokedAt is not null)
    {
        // Reuse of a rotated token: revoke that token's family only (replay protection).
        await db.RefreshTokens
            .Where(t => t.FamilyId == stored.FamilyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTime.UtcNow));
        await familyTx.CommitAsync();
        return Results.Json(
            new ErrorResponse([new ErrorDetail("REFRESH_TOKEN_REUSED", "Refresh token was already used. All sessions of this login were revoked.")]),
            statusCode: StatusCodes.Status401Unauthorized);
    }

    if (stored.ExpiresAt <= DateTime.UtcNow)
    {
        await familyTx.CommitAsync();
        return Results.Json(
            new ErrorResponse([new ErrorDetail("REFRESH_TOKEN_EXPIRED", "Refresh token has expired.")]),
            statusCode: StatusCodes.Status401Unauthorized);
    }

    // Atomic rotation in ONE transaction: generate the successor first, then
    // conditionally claim the old token and insert the replacement together.
    // Concurrent refreshes with the same token: the first update wins, the
    // second sees 0 rows and is treated as token reuse (family revoked).
    var now = DateTime.UtcNow;
    var newToken = JwtTokenService.GenerateRefreshToken();
    var newHash = JwtTokenService.HashRefreshToken(newToken);

    var claimed = await db.RefreshTokens
        .Where(t => t.Id == stored.Id && t.RevokedAt == null && t.ExpiresAt > now)
        .ExecuteUpdateAsync(s => s
            .SetProperty(t => t.RevokedAt, now)
            .SetProperty(t => t.ReplacedByTokenHash, newHash));
    if (claimed == 0)
    {
        // Lost the race OR expired between read and claim: treat as reuse so
        // the advertised replay policy does not depend on timing.
        await db.RefreshTokens
            .Where(t => t.FamilyId == stored.FamilyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now));
        await familyTx.CommitAsync();
        return Results.Json(
            new ErrorResponse([new ErrorDetail("REFRESH_TOKEN_REUSED", "Refresh token was already used. All sessions of this login were revoked.")]),
            statusCode: StatusCodes.Status401Unauthorized);
    }

    db.RefreshTokens.Add(new RefreshToken
    {
        UserId = stored.User.Id,
        TokenHash = newHash,
        FamilyId = stored.FamilyId,
        ExpiresAt = DateTime.UtcNow.Add(jwt.RefreshTokenLifetime),
        CreatedAt = DateTime.UtcNow,
    });
    await db.SaveChangesAsync();
    await familyTx.CommitAsync();
    SetRefreshCookie(http, newToken, jwt);

    // Browser flow (cookie): never return the raw refresh token in JSON — XSS cannot
    // read the HttpOnly cookie, and the rotation happens only through it.
    var viaCookie = string.IsNullOrWhiteSpace(req?.RefreshToken);
    return Results.Ok(new AuthResponse(
        jwt.CreateAccessToken(stored.User),
        viaCookie ? "" : newToken,
        new UserDto(stored.User.Id, stored.User.Email, stored.User.DisplayName, stored.User.Role)));
})
.AllowAnonymous()
.Produces<AuthResponse>(StatusCodes.Status200OK)
.Produces<ErrorResponse>(StatusCodes.Status401Unauthorized);

api.MapPost("/auth/logout", async (RecallDbContext db, HttpContext http, RefreshRequest? req) =>
{
    // Bearer OR refresh-cookie authentication: a browser whose 15-minute
    // access token has already expired must still be able to log out. The
    // valid refresh cookie identifies the user just as reliably.
    var presented = req?.RefreshToken ?? http.Request.Cookies["recall_refresh"];
    int? userId = http.User.UserId();
    if (userId is null && JwtTokenService.TryHashRefreshToken(presented, out var cookieHash))
    {
        userId = await db.RefreshTokens
            .Where(t => t.TokenHash == cookieHash && t.RevokedAt == null)
            .Select(t => (int?)t.UserId)
            .FirstOrDefaultAsync();
    }
    if (userId is null) return ApiHelpers.Unauthorized();

    if (!JwtTokenService.TryHashRefreshToken(presented, out var tokenHash))
    {
        http.Response.Cookies.Delete("recall_refresh", new CookieOptions { Path = "/api/v1/auth" });
        return Results.NoContent(); // unknown/garbage token: still a successful logout for the client
    }
    var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash && t.UserId == userId);
    if (stored is not null)
    {
        // Serialize against refresh/replay for this user, then revoke the
        // presented token's ENTIRE family — including when the presented token
        // was already rotated and its successor is still live.
        await using var familyTx = await db.Database.BeginTransactionAsync();
        await db.Users
            .FromSqlInterpolated($"SELECT * FROM \"Users\" WHERE \"Id\" = {stored.UserId} FOR UPDATE")
            .AsNoTracking()
            .LoadAsync();
        await db.RefreshTokens
            .Where(t => t.FamilyId == stored.FamilyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTime.UtcNow));
        await familyTx.CommitAsync();
    }

    http.Response.Cookies.Delete("recall_refresh", new CookieOptions { Path = "/api/v1/auth" });
    return Results.NoContent();
})
.Produces(StatusCodes.Status204NoContent)
.Produces<ErrorResponse>(StatusCodes.Status401Unauthorized);

void SetRefreshCookie(HttpContext http, string refreshToken, JwtTokenService jwt)
{
    http.Response.Cookies.Append("recall_refresh", refreshToken, new CookieOptions
    {
        HttpOnly = true,
        Secure = cookieSecure,
        SameSite = cookieSameSite,
        Expires = DateTimeOffset.UtcNow.Add(jwt.RefreshTokenLifetime),
        Path = "/api/v1/auth",
    });
}

static async Task<string> IssueRefreshTokenAsync(RecallDbContext db, JwtTokenService jwt, AppUser user, Guid? familyId = null)
{
    var token = JwtTokenService.GenerateRefreshToken();
    db.RefreshTokens.Add(new RefreshToken
    {
        UserId = user.Id,
        TokenHash = JwtTokenService.HashRefreshToken(token),
        FamilyId = familyId ?? Guid.NewGuid(),
        ExpiresAt = DateTime.UtcNow.Add(jwt.RefreshTokenLifetime),
        CreatedAt = DateTime.UtcNow,
    });
    await db.SaveChangesAsync();
    return token;
}

if (exportOpenApi)
    await SwaggerExport.WriteAsync(app, args[1]);
else
    app.Run();

// ---- Request DTOs ----

public partial class Program
{
    /// <summary>True when the exception is specifically a PostgreSQL unique-index violation.</summary>
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: "23505" };
}

public record CourseCreateRequest(string Code, string Title, string? Description, string? Visibility);
public record CourseUpdateRequest(string? Code, string? Title, string? Description, string? Visibility);
public record TopicCreateRequest(string Title, string? Summary, int? Position);
public record TopicUpdateRequest(string? Title, string? Summary, int? Position);
public record CardCreateRequest(string Question, string Answer, string? Hint, int? Difficulty, string? Tags);
public record CardUpdateRequest(string? Question, string? Answer, string? Hint, int? Difficulty, string? Tags);
public record ReviewRequest(string Rating);
public record CardImportItem(string Question, string Answer, string? Hint, int? Difficulty, string? Tags);
public record CardImportRequest(List<CardImportItem> Cards);
public record RegisterRequest(string Email, string Password, string DisplayName);
public record LoginRequest(string Email, string Password);
public record RefreshRequest(string RefreshToken);
public record UserDto(int Id, string Email, string DisplayName, string Role);
public record AuthResponse(string AccessToken, string RefreshToken, UserDto User);

public partial class Program { }
