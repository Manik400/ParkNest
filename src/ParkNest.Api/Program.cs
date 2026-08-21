using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
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

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.Run();

/// <summary>Exposed so integration tests can drive the API through <c>WebApplicationFactory</c>.</summary>
public partial class Program;
