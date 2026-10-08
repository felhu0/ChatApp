using ChatApp.Models;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;

namespace ChatApp.Services;

// TokenService skapar en JWT (JSON Web Token) när en användare har loggat in.
public class TokenService(IConfiguration config)
{
    public string CreateToken(User user)
    {
        // Hämtar den hemliga nyckeln från User Secrets. Utan den kan ingen token skapas.
        var keyBase64 = config["Jwt:Key"]
            ?? throw new InvalidProgramException("Jwt:Key saknas.");

        // Gör om nyckeln från Base64-text till bytes, i den form JWT-biblioteket kan signera med
        var key = new SymmetricSecurityKey(Convert.FromBase64String(keyBase64));

        var minutes = config.GetValue("Jwt:ExpiryMinutes", 30);

        //Beskriv vad token ska innehålla
        var descrptor = new SecurityTokenDescriptor
        {
            //Claims = uppgifter om användaren. Hämtas från databasen, inte från det användaren skrev.
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()), // sub: användarens id
                new Claim(JwtRegisteredClaimNames.Name, user.UserName),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())  // jti: unikt id för just den här token

                ]),

            //Utfärdare: vem som skapade token
            Issuer = config["Jwt:Issuer"],

            //Mottagare: vem token är avsedd för
            Audience = config["Jwt:Audience"],

            Expires = DateTime.UtcNow.AddMinutes(minutes),

            // Signerar token med Jwt:Key (serverns hemliga nyckel) och HMAC (Hash-based Message Authentication Code)-SHA (Secure Hash Algorithm)-256.
            // Nyckeln lämnar aldrig servern. Användarna får bara den färdiga token.
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        };

        // Bygg den färdiga token-strängen: header.payload.signatur
        return new JsonWebTokenHandler().CreateToken(descrptor);
    }
}
