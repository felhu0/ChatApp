namespace ChatApp.Models;

// Ett rum som skickas till webbläsaren.
public record RoomDto(
    string Id,
    string Name,
    string Type,          // "public" (lobbyn) eller "group" (grupprum)
    bool IsAdmin,         // true om användaren får bjuda in
    WrappedKeyDto? Key);  // inslagen rumsnyckel (rumsnyckeln krypterad åt just den här användaren), null för lobbyn
