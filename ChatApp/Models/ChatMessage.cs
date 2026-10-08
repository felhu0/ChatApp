namespace ChatApp.Models;

// Ett sparat chattmeddelande. Antingen Content (publikt rum) eller Ciphertext + Iv (grupprum).
public class ChatMessage
{
    public long Id { get; set; }
    public required string RoomId { get; set; }
    public Room Room { get; set; } = null!;

    public int SenderId { get; set; }
    public User Sender { get; set; } = null!;

    // Klartext – bara i publika rum (lobbyn)
    public string? Content { get; set; }

    // Krypterat med rumsnyckeln, AES-GCM (Advanced Encryption Standard – Galois/Counter Mode).
    // Bara i grupprum. Servern kan inte läsa det och lagrar aldrig nyckeln.
    public string? Ciphertext { get; set; }
    public string? Iv { get; set; } // IV (Initialization Vector)

    public DateTime SentAt { get; set; } = DateTime.UtcNow;
}
