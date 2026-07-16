using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using VirtualTickets.Api.Auth;
using VirtualTickets.Api.Data;
using VirtualTickets.Api.Middleware;
using VirtualTickets.Api.Services;
using VirtualTickets.Api.Services.Validation;

DotEnv.Load(Path.Combine(Directory.GetCurrentDirectory(), ".env"));

var builder = WebApplication.CreateBuilder(args);
const string DisplayCorsPolicy = "DisplayCors";

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.Configure<VirtualTicketsJwtOptions>(options =>
{
    var section = builder.Configuration.GetSection(VirtualTicketsJwtOptions.SectionName);
    section.Bind(options);

    options.Issuer = Environment.GetEnvironmentVariable("VIRTUAL_TICKETS_JWT_ISSUER") ?? options.Issuer;
    options.Audience = Environment.GetEnvironmentVariable("VIRTUAL_TICKETS_JWT_AUDIENCE") ?? options.Audience;
    options.SigningKey = Environment.GetEnvironmentVariable("VIRTUAL_TICKETS_JWT_SIGNING_KEY") ?? options.SigningKey;

    if (int.TryParse(Environment.GetEnvironmentVariable("VIRTUAL_TICKETS_JWT_MINUTES"), out var minutes))
    {
        options.Minutes = minutes;
    }
});
builder.Services.AddSingleton<IConfigureOptions<JwtBearerOptions>, VirtualTicketsJwtConfigurator>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();
builder.Services.AddAuthorization();
builder.Services.AddCors(options =>
{
    options.AddPolicy(DisplayCorsPolicy, policy =>
    {
        policy
            .WithOrigins(
                "http://localhost:3001",
                "http://45.77.54.107:10021",
                "http://45.77.54.107:10010")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.AddSingleton<TicketDb>();
builder.Services.AddSingleton<VirtualTicketDb>();
builder.Services.AddSingleton<TerminalAuthDb>();
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddScoped<TicketApplicationService>();
builder.Services.AddScoped<StakeValidator>();
builder.Services.AddScoped<AccountValidator>();
builder.Services.AddScoped<OddsValidator>();
builder.Services.AddScoped<SetValidator>();

var app = builder.Build();

app.Logger.LogInformation(
    "[virtual-tickets-api] Resolved ASPNETCORE_URLS={AspNetCoreUrls}; urls={Urls}.",
    Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "(not set)",
    builder.Configuration["urls"] ?? "(not set)");

var jwtOptions = app.Services.GetRequiredService<IOptions<VirtualTicketsJwtOptions>>().Value;
if (!jwtOptions.IsConfigured && app.Environment.IsDevelopment())
{
    app.Logger.LogWarning("[virtual-tickets-api] VIRTUAL_TICKETS_JWT_SIGNING_KEY is not set; terminal JWT authentication is disabled.");
}

app.UseSwagger();
app.UseSwaggerUI();

app.UseCors(DisplayCorsPolicy);
app.UseMiddleware<VirtualTicketsApiKeyMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

file static class DotEnv
{
    public static void Load(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}
