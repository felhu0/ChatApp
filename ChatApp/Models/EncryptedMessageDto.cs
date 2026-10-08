namespace ChatApp.Models;

// Ett krypterat meddelande som skickas mellan webbläsaren och servern. Krypterat med rumsnyckeln
// Servern kan skicka vidare och spara det, men inte läsa det.
public record EncryptedMessageDto(string Ciphertext, string Iv);