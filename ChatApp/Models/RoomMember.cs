namespace ChatApp.Models;

// Kopplar en användare till ett rum: "den här användaren får vara här"
// Servern kontrollerar behörighet mot den här tabellen.
public class RoomMember
{
    public required string RoomId { get; set; }
    public Room Room { get; set; } = null!;

    public int UserId { get; set; }
    public User User { get; set; } = null!;

    // Admin får bjuda in andra till grupprummet
    public bool IsAdmin { get; set; }
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;


    // ----- Inslagen rumsnyckel -----
    // Alla tre behövs för att medlemmen ska kunna låsa upp rumsnyckeln. Servern kan inte läsa dem.

    // Den låsta rumsnyckeln
    public string? WrappedKey { get; set; }

    // IV (Initialization Vector) som användes vid inslagningen
    public string? KeyIv { get; set; }

    // Tillfällig publik nyckel från inslagningen, ECIES (Elliptic Curve Integrated Encryption Scheme)
    public string? EphemeralPublicKey { get; set; }
}
