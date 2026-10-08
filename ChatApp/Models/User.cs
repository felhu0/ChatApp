namespace ChatApp.Models;

public class User
{
    public int Id { get; set; }

    public required string UserName { get; set; }

    // Samma namn i versaler ("ANNA"), så att "anna" och "Anna" räknas som samma användare
    public required string NormalizedUserName { get; set; }

    // Salt + hash från PBKDF2 (Password-Based Key Derivation Function 2). Lösenordet sparas aldrig.
    public required string PasswordHash { get; set; }

    // Publik nyckel (öppna delen av enhetsnyckeln). Används för att slå in rumsnycklar åt användaren.
    // Null tills användaren har loggat in första gången.
    // Den privata nyckeln (hemliga delen av enhetsnyckeln) lämnar aldrig webbläsaren.
    public string? PublicKey { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}