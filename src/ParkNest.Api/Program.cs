using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using ParkNest.Api;
using ParkNest.Api.Auth;
using ParkNest.Application.Abstractions;
using ParkNest.Application.Options;
using ParkNest.Domain.Common;
using ParkNest.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddParkNestInfrastructure(builder.Configuration, builder.Environment);

var authOptions = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>()!;

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = authOptions.Issuer,
            ValidAudience = authOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(authOptions.SigningKey)),
            // Default is 5 minutes, which lets a revoked-looking token linger. Sessions are long
            // enough that we don't need the slack.
            ClockSkew = TimeSpan.Zero
        };
    });

// Deny by default. Anything reachable without a token must opt out with [AllowAnonymous], so
// forgetting an attribute fails closed rather than exposing an endpoint.
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

// Enums cross the wire as their names, not their ordinals. Without this the API contradicts
// itself — hand-mapped fields already return strings via ToString(), while raw enum properties
// serialise as integers, so a client sees "role": 4 next to "status": "Completed". Names are also
// the stable contract: reordering an enum member would silently change every stored ordinal.
builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Rate limiting, at the edge, for everything reachable without a token. The service layer caps
// what a single phone number can cost us; this caps what a single caller can cost us, which is a
// different attack — one number under a flood of IPs versus one IP walking a list of numbers.
//
// Partitioned by remote IP. Behind a reverse proxy that means the proxy unless it is configured to
// forward the client address, so ForwardedHeaders has to be on before this is load-bearing in a
// real deployment.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // A ProblemDetails body, so a 429 from the middleware reads the same as a 429 from the
    // service layer. A client should not have to parse two shapes for one condition.
    options.OnRejected = async (context, cancellationToken) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.HttpContext.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Too many requests",
            Detail = "Slow down and try again shortly.",
            Type = nameof(TooManyRequestsException)
        }, cancellationToken);
    };

    // Sending an SMS costs money, so this is the tightest bucket in the system.
    options.AddPolicy(RateLimitPolicies.OtpRequest, http => RateLimitPartition.GetFixedWindowLimiter(
        ClientKey(http),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(15),
            QueueLimit = 0
        }));

    // Verification is free to serve but is the brute-force surface; the per-code attempt cap
    // already bounds guesses against one code, this bounds guesses across many.
    options.AddPolicy(RateLimitPolicies.OtpVerify, http => RateLimitPartition.GetFixedWindowLimiter(
        ClientKey(http),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 30,
            Window = TimeSpan.FromMinutes(15),
            QueueLimit = 0
        }));

    // The webhook is anonymous and every call costs an HMAC over the body before it can be
    // rejected. Generous, because a real gateway retries failed deliveries and must not be
    // throttled into giving up.
    options.AddPolicy(RateLimitPolicies.PaymentWebhook, http => RateLimitPartition.GetFixedWindowLimiter(
        ClientKey(http),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 120,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));

    static string ClientKey(HttpContext http) =>
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddProblemDetails();

// The Angular dev server runs on its own origin. Only registered outside Production, where the
// admin app is served same-origin and a permissive CORS policy would be pure attack surface.
const string devCorsPolicy = "parknest-dev-clients";

if (!builder.Environment.IsProduction())
{
    builder.Services.AddCors(options => options.AddPolicy(devCorsPolicy, policy => policy
        .WithOrigins("http://localhost:4200", "https://localhost:4200")
        .AllowAnyHeader()
        .AllowAnyMethod()));
}

builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "ParkNest API", Version = "v1" });

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste the access token from POST /api/auth/verify-otp."
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});

var storageOptions = builder.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>()
    ?? new StorageOptions();

var app = builder.Build();

// Failures are mapped centrally so controllers stay free of try/catch and clients get a stable
// error contract. Note the split: 401 means "who are you", 403 means "not yours", 400 means the
// request broke a business rule, 402 means the renter is short on credits.
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;

    var (status, title) = exception switch
    {
        UnauthorizedException => (StatusCodes.Status401Unauthorized, "Authentication required"),
        TooManyRequestsException => (StatusCodes.Status429TooManyRequests, "Too many requests"),
        ForbiddenException => (StatusCodes.Status403Forbidden, "Forbidden"),
        InsufficientCreditsException => (StatusCodes.Status402PaymentRequired, "Insufficient credits"),
        DomainException => (StatusCodes.Status400BadRequest, "Request rejected"),
        _ => (StatusCodes.Status500InternalServerError, "Unexpected error")
    };

    var problem = new ProblemDetails
    {
        Status = status,
        Title = title,
        // Never echo an internal exception message to the caller.
        Detail = status == StatusCodes.Status500InternalServerError ? null : exception?.Message,
        Type = exception?.GetType().Name
    };

    // Tell the caller when to come back rather than leaving them to guess and hammer.
    if (exception is TooManyRequestsException throttled)
    {
        context.Response.Headers.RetryAfter =
            ((int)Math.Ceiling(throttled.RetryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
    }

    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(problem);
}));

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (!app.Environment.IsProduction())
{
    app.UseCors(devCorsPolicy);
}

// Ahead of authentication: a flood should be shed before it costs us a signature validation.
app.UseRateLimiter();

// Listing photos, when they are kept on this box. Deliberately narrow:
//
//  - only the three extensions the upload path can produce, so anything else that ends up in the
//    directory is a 404 rather than something a browser will try to interpret;
//  - nosniff, so a file cannot be re-interpreted as script on the strength of its content;
//  - attachment-free but non-executing content types only, because these are served from the API's
//    own origin and user-supplied content served as active content is how stored XSS happens.
if (!storageOptions.IsDisabled)
{
    var mediaRoot = Path.IsPathRooted(storageOptions.LocalRoot)
        ? storageOptions.LocalRoot
        : Path.Combine(app.Environment.ContentRootPath, storageOptions.LocalRoot);

    Directory.CreateDirectory(mediaRoot);

    var contentTypes = new FileExtensionContentTypeProvider(new Dictionary<string, string>
    {
        [".jpg"] = "image/jpeg",
        [".png"] = "image/png",
        [".webp"] = "image/webp",
    });

    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(mediaRoot),
        RequestPath = '/' + storageOptions.PublicPath.Trim('/'),
        ContentTypeProvider = contentTypes,
        // Anything without one of the three mappings above is simply not served.
        ServeUnknownFileTypes = false,
        OnPrepareResponse = context =>
        {
            context.Context.Response.Headers.XContentTypeOptions = "nosniff";
            // Photos are immutable once written — the filename is a fresh GUID every upload — so
            // they can be cached hard.
            context.Context.Response.Headers.CacheControl = "public,max-age=31536000,immutable";
        },
    });
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.Run();

/// <summary>Exposed so integration tests can drive the API through <c>WebApplicationFactory</c>.</summary>
public partial class Program;
