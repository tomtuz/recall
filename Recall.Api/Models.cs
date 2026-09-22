using Microsoft.EntityFrameworkCore;
using Recall.Api.Auth;
using Recall.Api.Services;

namespace Recall.Api;

public class AppUser
{
    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Role { get; set; } = "Contributor"; // Guest is unauthenticated, never stored
    public string PasswordHash { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public enum CourseVisibility
{
    Private = 0,
    Public = 1,
}

public class Course
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public CourseVisibility Visibility { get; set; } = CourseVisibility.Private;
    public int OwnerId { get; set; }
    public AppUser Owner { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<Topic> Topics { get; set; } = new();
}

public class Topic
{
    public int Id { get; set; }
    public int CourseId { get; set; }
    public Course Course { get; set; } = null!;
    public string Title { get; set; } = "";
    public int Position { get; set; }
    public string Summary { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<Card> Cards { get; set; } = new();
}

public enum CardState
{
    New = 0,
    Learning = 1,
    Review = 2,
    Relearning = 3,
}

public class Card
{
    public int Id { get; set; }
    public int TopicId { get; set; }
    public Topic Topic { get; set; } = null!;
    public string Question { get; set; } = "";
    public string Answer { get; set; } = "";
    public string? Hint { get; set; }
    public int Difficulty { get; set; } = 3; // 1-5, subjective difficulty estimate
    public string Tags { get; set; } = ""; // comma-separated
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // SRS state (SM-2-lite) — updated only by the server on review
    public CardState State { get; set; } = CardState.New;
    public double Ease { get; set; } = 2.5;
    public int IntervalDays { get; set; } = 0;
    public int Reps { get; set; } = 0;
    public int Lapses { get; set; } = 0;
    public DateTime? DueAt { get; set; }
    public DateTime? LastReviewedAt { get; set; }
}

public class RecallDbContext(DbContextOptions<RecallDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Course> Courses => Set<Course>();
    public DbSet<Topic> Topics => Set<Topic>();
    public DbSet<Card> Cards => Set<Card>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppUser>(e =>
        {
            e.HasIndex(u => u.Email).IsUnique();
        });

        modelBuilder.Entity<Course>(e =>
        {
            e.HasIndex(c => c.Code);
            e.HasOne(c => c.Owner)
                .WithMany()
                .HasForeignKey(c => c.OwnerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Topic>(e =>
        {
            e.HasIndex(t => new { t.CourseId, t.Position });
            e.HasOne(t => t.Course)
                .WithMany(c => c.Topics)
                .HasForeignKey(t => t.CourseId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Card>(e =>
        {
            e.HasIndex(c => c.DueAt);
            e.HasIndex(c => new { c.TopicId });
            e.HasOne(c => c.Topic)
                .WithMany(t => t.Cards)
                .HasForeignKey(c => c.TopicId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RefreshToken>(e =>
        {
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasOne(t => t.User)
                .WithMany()
                .HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}

public static class DbSeed
{
    public static void EnsureSeeded(RecallDbContext db)
    {
        if (db.Users.Any() || db.Courses.Any()) return;

        var passwordHasher = new PasswordHasher();
        var admin = new AppUser { Email = "admin@recall.local", DisplayName = "Recall Admin", Role = "Administrator", PasswordHash = passwordHasher.Hash("Admin123!"), CreatedAt = DateTime.UtcNow };
        var author = new AppUser { Email = "author@recall.local", DisplayName = "Jonas Author", Role = "Contributor", PasswordHash = passwordHasher.Hash("Author123!"), CreatedAt = DateTime.UtcNow };
        var learner = new AppUser { Email = "learner@recall.local", DisplayName = "Ruta Learner", Role = "Contributor", PasswordHash = passwordHasher.Hash("Learner123!"), CreatedAt = DateTime.UtcNow };
        var giedre = new AppUser { Email = "giedre@recall.local", DisplayName = "Giedrė Kavaliauskaitė", Role = "Contributor", PasswordHash = passwordHasher.Hash("Giedre123!"), CreatedAt = DateTime.UtcNow };
        var marius = new AppUser { Email = "marius@recall.local", DisplayName = "Marius Petrauskas", Role = "Contributor", PasswordHash = passwordHasher.Hash("Marius123!"), CreatedAt = DateTime.UtcNow };
        db.Users.AddRange(admin, author, learner, giedre, marius);

        var webCourse = new Course { Code = "SAIT-2026", Title = "Web Application Design", Description = "REST, API design and web application architecture.", Visibility = CourseVisibility.Public, Owner = author, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var dbCourse = new Course { Code = "DB-101", Title = "Database Fundamentals", Description = "Relational model, SQL and transactions.", Visibility = CourseVisibility.Public, Owner = author, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var mathCourse = new Course { Code = "MATH-1", Title = "Linear Algebra", Description = "Vectors, matrices and their applications.", Visibility = CourseVisibility.Public, Owner = admin, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var historyCourse = new Course { Code = "HIST-2", Title = "History of Computing", Description = "From Babbage to the cloud.", Visibility = CourseVisibility.Public, Owner = admin, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var jsCourse = new Course { Code = "JS-DEV", Title = "JavaScript in Depth", Description = "Closures, event loop, async patterns.", Visibility = CourseVisibility.Public, Owner = author, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var privateCourse = new Course { Code = "PRIV-1", Title = "My Private Notes", Description = "Private course visible only to the owner.", Visibility = CourseVisibility.Private, Owner = learner, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Courses.AddRange(webCourse, dbCourse, mathCourse, historyCourse, jsCourse, privateCourse);

        var restTopic = new Topic { Course = webCourse, Title = "REST principles", Position = 1, Summary = "Resources, representations, uniform interface.", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var authTopic = new Topic { Course = webCourse, Title = "Authentication", Position = 2, Summary = "JWT, OAuth2, session management.", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var sqlTopic = new Topic { Course = dbCourse, Title = "SQL basics", Position = 1, Summary = "SELECT, JOIN, GROUP BY.", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var normTopic = new Topic { Course = dbCourse, Title = "Normalization", Position = 2, Summary = "1NF, 2NF, 3NF.", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var vectorTopic = new Topic { Course = mathCourse, Title = "Vectors", Position = 1, Summary = "Vector operations and geometry.", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var matrixTopic = new Topic { Course = mathCourse, Title = "Matrices", Position = 2, Summary = "Matrix multiplication, determinants.", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var earlyTopic = new Topic { Course = historyCourse, Title = "Early computing", Position = 1, Summary = "Babbage, Lovelace, ENIAC.", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var modernTopic = new Topic { Course = historyCourse, Title = "Modern era", Position = 2, Summary = "Personal computers, the web, cloud.", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var closuresTopic = new Topic { Course = jsCourse, Title = "Closures", Position = 1, Summary = "Lexical scope and closure patterns.", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var asyncTopic = new Topic { Course = jsCourse, Title = "Async patterns", Position = 2, Summary = "Promises, async/await, event loop.", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var secretTopic = new Topic { Course = privateCourse, Title = "Draft ideas", Position = 1, Summary = "Not published anywhere else.", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.Topics.AddRange(restTopic, authTopic, sqlTopic, normTopic, vectorTopic, matrixTopic, earlyTopic, modernTopic, closuresTopic, asyncTopic, secretTopic);

        var cards = new List<Card>
        {
            new() { Topic = restTopic, Question = "What does stateless mean in REST?", Answer = "Each request contains all information needed to process it; the server keeps no client session state.", Hint = "Think about what the server stores between requests.", Difficulty = 2, Tags = "rest,theory", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new() { Topic = restTopic, Question = "Which HTTP method should create a resource?", Answer = "POST to the resource collection URI; the server assigns the id and returns 201 with a Location header.", Hint = "Not GET.", Difficulty = 1, Tags = "rest,http", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new() { Topic = restTopic, Question = "What is HATEOAS?", Answer = "Hypermedia as the Engine of Application State: responses include links so the client can discover available transitions.", Hint = "Hypermedia controls.", Difficulty = 4, Tags = "rest,hypermedia", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new() { Topic = restTopic, Question = "Why should PUT be idempotent?", Answer = "So that repeating the same request has the same effect as performing it once — safe for retries.", Hint = "Idempotence definition.", Difficulty = 3, Tags = "rest,http", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new() { Topic = restTopic, Question = "Give an example of a hierarchical REST URI.", Answer = "GET /courses/3/topics/7/cards — the path scopes the collection by its parents.", Hint = "Nesting.", Difficulty = 2, Tags = "rest,uri", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new() { Topic = restTopic, Question = "Which status code means the resource was created?", Answer = "201 Created, with the Location header pointing at the new resource.", Hint = "2xx family.", Difficulty = 1, Tags = "rest,status", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new() { Topic = authTopic, Question = "What claims does a typical JWT access token carry?", Answer = "iss, sub (user id), role, iat and exp at minimum.", Hint = "Think of the payload fields.", Difficulty = 2, Tags = "auth,jwt", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new() { Topic = authTopic, Question = "Why use short-lived access tokens with refresh tokens?", Answer = "A stolen access token expires quickly; the refresh token is stored server-side and can be rotated or revoked.", Hint = "Blast radius.", Difficulty = 3, Tags = "auth,jwt", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new() { Topic = authTopic, Question = "What is OAuth2 used for?", Answer = "Delegated authorization: granting a client limited access to resources without sharing the owner's credentials.", Hint = "Authorization, not authentication.", Difficulty = 3, Tags = "auth,oauth", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new() { Topic = authTopic, Question = "Where should a refresh token be stored in a browser?", Answer = "In an HttpOnly, Secure cookie so JavaScript cannot read it; never in localStorage.", Hint = "XSS.", Difficulty = 2, Tags = "auth,security", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new() { Topic = authTopic, Question = "What does 403 mean?", Answer = "The server understood the request and the caller is authenticated, but refuses to authorize the action.", Hint = "Not 401.", Difficulty = 1, Tags = "auth,status", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new() { Topic = sqlTopic, Question = "What does GROUP BY do?", Answer = "Groups rows with equal values so aggregate functions (COUNT, SUM, AVG) can be applied per group.", Hint = "Aggregation.", Difficulty = 2, Tags = "sql,aggregation", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new() { Topic = sqlTopic, Question = "Difference between INNER and LEFT JOIN?", Answer = "INNER JOIN keeps only matching rows; LEFT JOIN keeps all left rows, filling the right side with NULLs when there is no match.", Hint = "Unmatched rows.", Difficulty = 2, Tags = "sql,join", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new() { Topic = sqlTopic, Question = "What is a primary key?", Answer = "A column or column set that uniquely identifies each row; it is NOT NULL and unique.", Hint = "Identity.", Difficulty = 1, Tags = "sql,modeling", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new() { Topic = sqlTopic, Question = "Why use parameterized queries?", Answer = "To prevent SQL injection — user input is passed as data, never concatenated into SQL text.", Hint = "Injection.", Difficulty = 2, Tags = "sql,security", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new() { Topic = sqlTopic, Question = "What does an index trade off?", Answer = "Faster reads (lookups, joins) in exchange for extra storage and slower writes.", Hint = "No free lunch.", Difficulty = 3, Tags = "sql,performance", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new() { Topic = normTopic, Question = "What does 3NF require?", Answer = "Every non-key column depends on the key, the whole key and nothing but the key.", Hint = "Transitive dependencies.", Difficulty = 4, Tags = "db,normalization", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new() { Topic = normTopic, Question = "What is a transitive dependency?", Answer = "When a non-key column depends on another non-key column instead of the key alone.", Hint = "A -> B -> C.", Difficulty = 4, Tags = "db,normalization", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new() { Topic = normTopic, Question = "Why normalize at all?", Answer = "To reduce redundancy and update/insert/delete anomalies.", Hint = "Anomalies.", Difficulty = 3, Tags = "db,normalization", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new() { Topic = normTopic, Question = "When is denormalization acceptable?", Answer = "For read performance in reporting or hot paths, done consciously after measuring.", Hint = "Trade-off.", Difficulty = 4, Tags = "db,normalization", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new() { Topic = vectorTopic, Question = "What is the dot product used for?", Answer = "Computing projections and testing orthogonality: a·b = |a||b|cos(theta).", Hint = "Projection.", Difficulty = 3, Tags = "math,vectors", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new() { Topic = vectorTopic, Question = "When are two vectors orthogonal?", Answer = "When their dot product is zero (and both are non-zero).", Hint = "90 degrees.", Difficulty = 2, Tags = "math,vectors", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new() { Topic = matrixTopic, Question = "What does a determinant of 0 mean?", Answer = "The matrix is singular — not invertible; the columns are linearly dependent.", Hint = "Invertibility.", Difficulty = 3, Tags = "math,matrices", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new() { Topic = matrixTopic, Question = "What is matrix multiplication, informally?", Answer = "Rows of the first matrix dot-multiplied with columns of the second.", Hint = "Row times column.", Difficulty = 2, Tags = "math,matrices", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new() { Topic = earlyTopic, Question = "Who is considered the first programmer?", Answer = "Ada Lovelace — she wrote an algorithm for Babbage's Analytical Engine.", Hint = "Analytical Engine.", Difficulty = 2, Tags = "history", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new() { Topic = earlyTopic, Question = "What was ENIAC?", Answer = "The first general-purpose electronic computer (1945), programmed by rewiring panels.", Hint = "Electronic.", Difficulty = 2, Tags = "history", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new() { Topic = modernTopic, Question = "What did the WWW popularize?", Answer = "Hypertext documents linked over the internet via HTTP and URLs.", Hint = "Tim Berners-Lee, 1989.", Difficulty = 1, Tags = "history,web", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new() { Topic = modernTopic, Question = "What defines cloud computing?", Answer = "On-demand, elastic, metered computing resources managed by a provider.", Hint = "NIST definition.", Difficulty = 3, Tags = "history,cloud", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new() { Topic = closuresTopic, Question = "What is a closure?", Answer = "A function together with the captured variables from its defining lexical scope.", Hint = "Function + environment.", Difficulty = 3, Tags = "js,closures", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new() { Topic = closuresTopic, Question = "Why does a loop variable leak in classic var-based closures?", Answer = "var is function-scoped, so all closures share the same binding; let gives each iteration its own.", Hint = "var vs let.", Difficulty = 4, Tags = "js,closures", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new() { Topic = asyncTopic, Question = "What does the event loop enable?", Answer = "Single-threaded concurrency: async operations resume via callbacks when their I/O completes.", Hint = "Non-blocking.", Difficulty = 3, Tags = "js,async", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new() { Topic = asyncTopic, Question = "What does async/await compile down to?", Answer = "Promise chains — await is syntax sugar over .then/.catch continuations.", Hint = "Sugar.", Difficulty = 2, Tags = "js,async", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
            new() { Topic = secretTopic, Question = "Why is this card private?", Answer = "Its course is private, so only the owner can see it.", Hint = "Visibility.", Difficulty = 1, Tags = "meta", CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
        };

        // Give some cards an in-progress SRS state so the review feature has data.
        var baseTime = DateTime.UtcNow;
        for (var i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            if (i % 4 == 1)
            {
                card.State = CardState.Review;
                card.Reps = 2;
                card.IntervalDays = 3;
                card.DueAt = baseTime.AddDays(-1); // due yesterday
                card.LastReviewedAt = baseTime.AddDays(-4);
            }
            else if (i % 4 == 2)
            {
                card.State = CardState.Learning;
                card.Reps = 1;
                card.IntervalDays = 0;
                card.DueAt = baseTime.AddHours(-6); // due earlier today
                card.LastReviewedAt = baseTime.AddHours(-8);
            }
            else if (i % 4 == 3)
            {
                card.State = CardState.Review;
                card.Reps = 4;
                card.IntervalDays = 10;
                card.DueAt = baseTime.AddDays(5); // not yet due
                card.LastReviewedAt = baseTime.AddDays(-5);
            }
        }

        db.Cards.AddRange(cards);
        db.SaveChanges();
    }
}