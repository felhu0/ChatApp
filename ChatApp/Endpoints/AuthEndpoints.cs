using ChatApp.Services;
using ChatApp.Data;
using ChatApp.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace ChatApp.Endpoints;

// Endpoints för registrering och inloggning under /api/auth.
public static partial class AuthEndpoints
{
    private const int MaxNameLength = 30;
    private const int MinPasswordLength = 8;
    private const int MaxPasswordLength = 128; // max skyddar mot extremt långa lösenord som gör hashningen långsam

    // Låtsasanvändare: hashas när användaren inte finns, så att svarstiden blir lika lång.
    // Skyddar mot timing-attacker som avslöjar vilka användarnamn som finns.
    private static readonly User DummyUser = new() { UserName = "", NormalizedUserName = "", PasswordHash = "" };

    // Endast a–ö, siffror, _ och -. Stoppar osynliga tecken, radbrytningar och tecken som ser likadana ut.
    [GeneratedRegex("^[a-zA-Z0-9åäöÅÄÖ_-]+$")]
    private static partial Regex ValidUserName();

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth");

        // Rate limiting: begränsar antal försök, skyddar mot gissning av lösenord (brute force)
        group.MapPost("/register", Register).RequireRateLimiting("auth");
        group.MapPost("/login", Login).RequireRateLimiting("auth");
    }

    private static async Task<IResult> Register(
        AuthRequest request, AppDbContext db, IPasswordHasher<User> hasher, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("ChatApp.Auth");
        var userName = request.Username?.Trim() ?? "";
        var password = request.Password ?? ""; // trimmas inte: mellanslag kan vara en del av lösenordet

        // Servern validerar alltid. Klienten kan aldrig litas på.
        if (userName.Length is 0 or > MaxNameLength || !ValidUserName().IsMatch(userName))
            return Results.Text($"Användarnamnet måste vara 1–{MaxNameLength} tecken (bokstäver, siffror, _ eller -).", statusCode: 400);

        if (password.Length < MinPasswordLength || password.Length > MaxPasswordLength)
            return Results.Text($"Lösenordet måste vara {MinPasswordLength}–{MaxPasswordLength} tecken.", statusCode: 400);

        // Versaler så att "Anna" och "anna" räknas som samma namn
        var normalized = userName.ToUpperInvariant();

        // // Ger ett tydligt felmeddelande om namnet är taget
        if (await db.Users.AnyAsync(u => u.NormalizedUserName == normalized))
            return Results.Text("Användarnamnet är upptaget.", statusCode: 409);

        var user = new User
        {
            UserName = userName,
            NormalizedUserName = normalized,
            PasswordHash = "" // sätts på nästa rad
        };

        // Slumpar salt, kör PBKDF2 (Password-Based Key Derivation Function 2) och sparar salt + hash som en sträng.
        // Själva lösenordet sparas aldrig.
        user.PasswordHash = hasher.HashPassword(user, password);

        db.Users.Add(user);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Unika indexet stoppar en dubblett om två registrerar samma namn samtidigt
            return Results.Text("Användarnamnet är upptaget.", statusCode: 409);
        }

        logger.LogInformation("User {UserName} registered", user.UserName);

        return Results.StatusCode(StatusCodes.Status201Created);
    }

    //Loggar in användaren och returnerar en JWT om namn och lösenord stämmer
    private static async Task<IResult> Login(
        AuthRequest request, AppDbContext db, IPasswordHasher<User> hasher, TokenService tokens,
        HttpContext http, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("ChatApp.Auth");
        var userName = request.Username?.Trim() ?? "";
        var password = request.Password ?? "";

        if (userName.Length is 0 or > MaxNameLength || password.Length is 0 or > MaxPasswordLength)
        {
            // Misstänkt indata: logga (rensat) men svara som ett vanligt fel
            logger.LogWarning("Failed login with invalid input for {UserName} from {IpAddress}",
                LogSanitizer.Clean(userName), http.Connection.RemoteIpAddress);
            return InvalidLogin();
        }

        var normalized = userName.ToUpperInvariant();
        var user = await db.Users.SingleOrDefaultAsync(u => u.NormalizedUserName == normalized);

        // Användaren finns inte: hasha ändå, så att svarstiden blir lika lång (skydd mot timing-attack)
        if (user is null)
        {
            hasher.HashPassword(DummyUser, password);

            // Rensas före loggning: skydd mot log injection
            logger.LogWarning("Failed login for {UserName} from {IpAddress}",
                LogSanitizer.Clean(userName), http.Connection.RemoteIpAddress);
            return InvalidLogin();
        }

        //Jämför lösenordet med den sparade hashen
        var result = hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
        {
            // Användarnamnet är inte validerat här, därför rensas det innan det loggas
            logger.LogWarning("Failed login for {UserName} from {IpAddress}",
                LogSanitizer.Clean(userName), http.Connection.RemoteIpAddress);
            return InvalidLogin();
        }

        // Om .NET har uppgraderat sina säkerhetskrav (t.ex. fler iterationer), 
        // skapas och sparas en ny hash. Den nya hashen är starkare och svårare för en hackare att knäcka.
        if (result == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = hasher.HashPassword(user, password);
            await db.SaveChangesAsync();
        }

        // Bara användarnamnet loggas, aldrig lösenord eller token
        logger.LogInformation("User {UserName} logged in", user.UserName);

        // Skapa en signerad token som klienten skickar med i varje anrop
        return Results.Ok(new LoginResponse(tokens.CreateToken(user), user.UserName));
    }

    // Samma svar oavsett om namnet eller lösenordet är fel, så att inget avslöjas
    private static IResult InvalidLogin() =>
        Results.Text("Fel användarnamn eller lösenord.", statusCode: 401);
}
