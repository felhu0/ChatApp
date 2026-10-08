namespace ChatApp.Models;

// Ett meddelande från chattloggen. Bara ett fält är ifyllt per meddelande:
// Content i lobbyn, Encrypted i grupprum.
public record MessageDto(string User, string? Content, EncryptedMessageDto? Encrypted, DateTime SentAt);

