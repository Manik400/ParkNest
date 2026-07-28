using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using ParkNest.Domain.Common;
using ParkNest.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddParkNestInfrastructure(builder.Configuration);
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails();

var app = builder.Build();

// Business-rule failures are client errors, not 500s. Mapping them centrally keeps controllers
// free of try/catch and gives mobile clients a stable error contract.
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;

    var (status, title) = exception switch
    {
        InsufficientCreditsException => (StatusCodes.Status402PaymentRequired, "Insufficient credits"),
        DomainException => (StatusCodes.Status400BadRequest, "Request rejected"),
        _ => (StatusCodes.Status500InternalServerError, "Unexpected error")
    };

    var problem = new ProblemDetails
    {
        Status = status,
        Title = title,
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

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

/// <summary>Exposed so integration tests can drive the API through <c>WebApplicationFactory</c>.</summary>
public partial class Program;
