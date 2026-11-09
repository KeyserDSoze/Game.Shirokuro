using Shirokuro.Application.Preview;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<BoardPreviewService>();

if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(options =>
        options.AddPolicy("local-dev", policy =>
            policy.WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
                  .AllowAnyHeader()
                  .AllowAnyMethod()));
}

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    app.UseCors("local-dev");
}

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", application = "Shirokuro" }));
app.MapGet("/api/preview/board", (int? radius, BoardPreviewService board) =>
{
    var requestedRadius = radius ?? 2;
    if (requestedRadius is < 1 or > 6)
    {
        return Results.BadRequest(new { code = "invalid_preview_radius" });
    }

    return Results.Ok(new
    {
        radius = requestedRadius,
        cells = board.Create(requestedRadius).Select(c => new { q = c.Q, r = c.R, s = c.S })
    });
});

app.Run();

public partial class Program;

