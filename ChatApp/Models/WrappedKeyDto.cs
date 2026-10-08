namespace ChatApp.Models;

// Inslagen rumsnyckel (rumsnyckeln krypterad åt en viss medlem), som skickas mellan webbläsaren och servern.
// Servern kan spara och skicka vidare den, men aldrig låsa upp den.
public record WrappedKeyDto(
    string WrappedKey,          // den låsta rumsnyckeln
    string KeyIv,               // IV (Initialization Vector) från inslagningen
    string EphemeralPublicKey); // tillfällig publik nyckel, behövs för att låsa upp