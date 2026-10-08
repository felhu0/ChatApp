using ChatApp.Models;

namespace ChatApp;

public interface IChatClient
{
    // Vanligt meddelande i klartext (lobbyn)
    Task ReceiveMessage(string roomId, string user, string message);

    // Systemmeddelande, t.ex. "Anna has joined"
    Task ReceiveSystemMessage(string roomId, string message);

    // Användaren har fått tillgång till ett nytt rum (skapat eller inbjuden),
    // tillsammans med sin inslagna rumsnyckel
    Task RoomAdded(RoomDto room);

    // Krypterat meddelande i ett grupprum.
    // Webbläsaren låser upp det med rumsnyckeln.
    Task ReceiveEncryptedMessage(string roomId, string user, EncryptedMessageDto message);
}
