using ChatApp.Data;
using ChatApp.Endpoints;
using ChatApp.Hubs;
using ChatApp.Models;
using ChatApp.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using System.Security.Claims;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Singleton: en enda instans för hela appen, så att hinkarna i rate limiting finns kvar mellan anrop
builder.Services.AddSingleton<HubRateLimitFilter>();

// Serilog ersätter standardloggningen. Inställningarna läses från "Serilog" i appsettings.json.
builder.Services.AddSerilog((services, config) => config
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

builder.Services.AddSignalR(options =>
{
    // Max storlek på ett inkommande meddelande. Större meddelanden stänger anslutningen.
    options.MaximumReceiveMessageSize = 16 * 1024;

    // Rate limiting körs före varje hubbanrop
    options.AddFilter<HubRateLimitFilter>();
});

builder.Services.AddSingleton<RoomService>();

// Vilka rum varje anslutning är med i (i minnet). Singleton: delas av alla anslutningar.
builder.Services.AddSingleton<IUserIdProvider, SubUserIdProvider>();

// Databasen (SQLite). Adressen läses från "ConnectionStrings:Default" i appsettings.json.
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default")));

// Hashning av lösenord med PBKDF2 (Password-Based Key Derivation Function 2) och slumpad salt
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();

// Skapar JWT vid inloggning
builder.Services.AddSingleton<TokenService>();

// ===== Inloggning med JWT (JSON Web Token) =====

// Jwt:Key (serverns hemliga nyckel som signerar tokens) läses från User Secrets.
// Appen startar inte utan den.
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new InvalidOperationException("Jwt:Key saknas i konfigurationen.");

// Registrera autentisering med JWT som standardmetod.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    // Behåll claim - namnen som de är i token("sub", "name"), istället för att .NET döper om dem
    options.MapInboundClaims = false;

    // Reglerna för vad en giltig token är
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"],            // rätt utfärdare
        ValidateAudience = true,
        ValidAudience = builder.Configuration["Jwt:Audience"],        // rätt mottagare
        ValidateLifetime = true,                                      // inte utgången
        ValidateIssuerSigningKey = true,                              // signaturen stämmer med Jwt:Key
        IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(jwtKey)),

        // Accepterar bara HS256 (HMAC-SHA-256), så att en angripare inte kan byta algoritm i token
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],

        // Standard är 5 minuters tolerans. 30 sekunder gör utgångstiden striktare. Utan raden är en token med 30 minuters giltighet i praktiken giltig i 35 minuter.
        ClockSkew = TimeSpan.FromSeconds(30),

        // Context.User.Identity.Name hämtas från claimet "name"
        NameClaimType = JwtRegisteredClaimNames.Name,
    };

    options.Events = new JwtBearerEvents
    {
        // WebSocket kan inte skicka headers, så SignalR skickar token i URL:en (?access_token=...).
        // Här plockas den ut – men bara för hubben, inte för andra adresser.
        OnMessageReceived = context =>
        {
            var token = context.Request.Query["access_token"]; // Läser värdet ur URL:en
            if (!string.IsNullOrEmpty(token) &&                // finns en token i URL:en?
                context.HttpContext.Request.Path.StartsWithSegments("/chat")) // och gäller det hubben?
            {
                context.Token = token;   // använd den
            }
            return Task.CompletedTask;
        }
    };
});

builder.Services.AddAuthorization();

// ===== Rate limiting för inloggning och registrering =====

// Rate limiting för inloggning och registrering: max 5 försök per minut och IP-adress. Skydd mot gissning av lösenord (brute force).
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;  // 429 = för många förfrågningar

    options.OnRejected = async (context, token) =>
    {
        // Loggar att gränsen nåddes – IP-adressen behövs för att upptäcka brute force
        context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>()
            .LogWarning("Rate limit reached for {Path} from {IpAddress}",
                context.HttpContext.Request.Path, context.HttpContext.Connection.RemoteIpAddress);

        context.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
        await context.HttpContext.Response.WriteAsync("För många försök. Vänta en minut och försök igen.", token);
    };

    // Policyn "auth" används av /register och /login i AuthEndpoints
    options.AddPolicy("auth", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,                     // 5 anrop
                Window = TimeSpan.FromMinutes(1),    // per minut    
                QueueLimit = 0                       // inga anrop får vänta i kö: över gränsen nekas direkt med "För många försök"
            }));
});

var app = builder.Build();

// ===== Ordningen för varje förfrågan (middleware) =====

// HSTS (HTTP Strict Transport Security): webbläsaren vägrar okrypterad HTTP i framtiden.
// Bara utanför Development, så att det inte låser localhost under utveckling.
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

// Skickar vidare HTTP-anrop till HTTPS
app.UseHttpsRedirection();

// En loggrad per förfrågan. RequestPath innehåller inte query-strängen, så access_token loggas aldrig.
app.UseSerilogRequestLogging();

// Visar index.html och filerna i wwwroot
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRateLimiter();

// Läser och validerar token, och kontrollerar sedan [Authorize].
app.UseAuthentication();
app.UseAuthorization();

// Kopplar hubben till /chat. Anslutningen stängs när token går ut.
app.MapHub<ChatHub>("/chat", options =>
{
    options.CloseOnAuthenticationExpiration = true;
});

// /api/auth/register och /api/auth/login
app.MapAuthEndpoints();

app.Run();