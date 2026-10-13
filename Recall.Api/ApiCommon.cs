using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Recall.Api.Services;

namespace Recall.Api;

/// <summary>Route values used in the _links objects so lists are self-describing.</summary>
public record Link(string Href, string Method);

public static class Links
{
    public static Link Self(string href) => new(href, "GET");
    public static Link Post(string href) => new(href, "POST");
    public static Link Patch(string href) => new(href, "PATCH");
    public static Link Delete(string href) => new(href, "DELETE");
}

public record PagedMeta(int Page, int PageSize, int TotalItems, int TotalPages);

/// <summary>Standard success envelope for lists: data + hypermedia + pagination meta.</summary>
public record ListResponse<T>(List<T> Data, Dictionary<string, Link> Links, PagedMeta Meta);

public record ErrorDetail(string Code, string Message);

/// <summary>Standard error envelope for every 4xx response, as required by the rubric.</summary>
public record ErrorResponse(List<ErrorDetail> Errors);

/// <summary>Flat DTO shapes returned by the API — decoupled from EF entities.</summary>
public record CourseDto(int Id, string Code, string Title, string Description, string Visibility, int OwnerId, string OwnerName, DateTime CreatedAt, DateTime UpdatedAt);
public record TopicDto(int Id, int CourseId, string CourseTitle, string Title, int Position, string Summary, DateTime CreatedAt, DateTime UpdatedAt);
public record CardDto(int Id, int TopicId, string TopicTitle, string Question, string Answer, string? Hint, int Difficulty, string Tags, string State, double Ease, int IntervalDays, int Reps, int Lapses, DateTime? DueAt, DateTime? LastReviewedAt, DateTime CreatedAt, DateTime UpdatedAt);

// Typed composite/review responses: runtime returns these records and the
// OpenAPI contract gets real success-response schemas from the same shapes.
public record DifficultyMixDto(double Average, int Hardest);

public record TopicOverviewDto(int Id, string Title, int Position, int CardCount, int DueCount);

public record CourseOverviewDto(
    CourseDto Course, int TopicCount, int CardCount, int DueCount,
    Dictionary<string, int> StateBreakdown,
    DifficultyMixDto DifficultyMix, List<TopicOverviewDto> Topics);

public record DashboardDto(
    int DueToday, int NewCount, int LearningCount, int ReviewCount,
    int CourseCount, Dictionary<string, int> StateBreakdown);

public record ReviewQueueItemDto(CardDto Card, Dictionary<string, Link> Links);

public record ReviewQueueDto(List<ReviewQueueItemDto> Data, int Count);

public record CardReviewDto(CardDto Card, string AppliedRating, DateTime NextDueAt, int IntervalDays);

public record CardImportDto(int Imported, List<CardDto> Cards);

public static class Mappers
{
    public static CourseDto ToDto(this Course c) =>
        new(c.Id, c.Code, c.Title, c.Description, c.Visibility.ToString(), c.OwnerId,
            c.Owner?.DisplayName ?? "", c.CreatedAt, c.UpdatedAt);
    public static TopicDto ToDto(this Topic t) => new(t.Id, t.CourseId, t.Course.Title, t.Title, t.Position, t.Summary, t.CreatedAt, t.UpdatedAt);
    public static CardDto ToDto(this Card c) => new(c.Id, c.TopicId, c.Topic.Title, c.Question, c.Answer, c.Hint, c.Difficulty, c.Tags, c.State.ToString(), c.Ease, c.IntervalDays, c.Reps, c.Lapses, c.DueAt, c.LastReviewedAt, c.CreatedAt, c.UpdatedAt);
}

/// <summary>Shared helpers: paging, validation errors, access control.</summary>
public static class ApiHelpers
{
    public const int MaxPageSize = 100;

    public static (int Page, int PageSize, int Offset) ParsePaging(int? page, int? pageSize)
    {
        var p = Math.Max(1, page ?? 1);
        var size = Math.Clamp(pageSize ?? 10, 1, MaxPageSize);
        // Guard against int overflow in the offset calculation (page near int.MaxValue).
        var maxPage = int.MaxValue / size;
        if (p > maxPage) p = maxPage;
        return (p, size, (p - 1) * size);
    }

    public static PagedMeta BuildMeta(int page, int pageSize, int totalItems) =>
        new(page, pageSize, totalItems, (int)Math.Ceiling(totalItems / (double)pageSize));

    public static IResult ValidationError(string field, string message) => Results.Json(
        new ErrorResponse([new ErrorDetail("VALIDATION_ERROR", $"{field}: {message}")]),
        statusCode: StatusCodes.Status400BadRequest);

    public static IResult NotFound(string resource, object id) => Results.Json(
        new ErrorResponse([new ErrorDetail("NOT_FOUND", $"{resource} '{id}' was not found.")]),
        statusCode: StatusCodes.Status404NotFound);

    public static IResult Conflict(string message) => Results.Json(
        new ErrorResponse([new ErrorDetail("CONFLICT", message)]),
        statusCode: StatusCodes.Status409Conflict);

    public static IResult Forbidden() => Results.Json(
        new ErrorResponse([new ErrorDetail("FORBIDDEN", "You do not own this resource.")]),
        statusCode: StatusCodes.Status403Forbidden);

    public static IResult Unauthorized() => Results.Json(
        new ErrorResponse([new ErrorDetail("UNAUTHORIZED", "Authentication is required.")]),
        statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>Guests see public courses only; owners and admins see their own private ones too.</summary>
    public static IQueryable<Course> ScopeCourses(RecallDbContext db, ClaimsPrincipal user)
    {
        var userId = user.UserId();
        var isAdmin = user.IsAdmin();
        var query = db.Courses.Include(c => c.Owner).AsQueryable();
        if (isAdmin) return query;
        if (userId is null) return query.Where(c => c.Visibility == CourseVisibility.Public);
        return query.Where(c => c.Visibility == CourseVisibility.Public || c.OwnerId == userId);
    }

    public static int? UserId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue("sub"), out var id) ? id : null;

    public static bool IsAdmin(this ClaimsPrincipal user) =>
        user.FindFirstValue("role") == "Administrator";
}

public static class Validation
{
    public static string? CheckTitle(string? value, int maxLength, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value)) return $"{fieldName} is required.";
        if (value.Trim().Length > maxLength) return $"{fieldName} must be at most {maxLength} characters.";
        return null;
    }

    public static string? CheckDifficulty(int? difficulty)
    {
        if (difficulty is null) return null;
        if (difficulty < 1 || difficulty > 5) return "Difficulty must be between 1 and 5.";
        return null;
    }

    /// <summary>PATCH semantics: validate only supplied fields, never accept empty values.</summary>
    public static string? CheckOptionalTitle(string? value, int maxLength, string fieldName)
    {
        if (value is null) return null;
        if (string.IsNullOrWhiteSpace(value)) return $"{fieldName} cannot be empty.";
        if (value.Trim().Length > maxLength) return $"{fieldName} must be at most {maxLength} characters.";
        return null;
    }

    /// <summary>PATCH semantics: validate only supplied fields. Optional text allows empty values ("" = clear); length cap still applies.</summary>
    public static string? CheckOptionalText(string? value, int maxLength, string fieldName)
    {
        if (value is null) return null;
        if (value.Trim().Length > maxLength) return $"{fieldName} must be at most {maxLength} characters.";
        return null;
    }

    public static string? CheckVisibility(string? value)
    {
        if (value is null) return null;
        if (value != "Public" && value != "Private") return "Visibility must be 'Public' or 'Private'.";
        return null;
    }

    public const int MaxPasswordLength = 128;
    public const int MaxImportCards = 200;
    public static readonly string[] CourseSorts = ["title", "-title", "code"];

    /// <summary>Query filter: a defined card state name (case-insensitive); numbers are rejected.</summary>
    public static string? CheckCardState(string? value, out CardState? state)
    {
        state = null;
        if (value is null) return null;
        if (value.Length == 0 || char.IsDigit(value[0]) || value[0] is '-' or '+'
            || !Enum.TryParse<CardState>(value, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            return $"State must be one of: {string.Join(", ", Enum.GetNames<CardState>())}.";
        state = parsed;
        return null;
    }

    public static bool IsValidEmail(string value)
    {
        // Simple student-level check: exactly one @, non-empty local part and domain with a dot.
        var at = value.IndexOf('@');
        return at > 0 && value.LastIndexOf('@') == at && value.Length - at > 3 && value[(at + 1)..].Contains('.');
    }
}