using System.Collections.Concurrent;
using ChatApp.Models;

namespace ChatApp.Services;

// Håller reda på vilka rum varje anslutning är med i just nu, och rummens typ.
// Finns bara i minnet, så att servern slipper fråga databasen vid varje meddelande.
// Behörigheten kontrolleras mot databasen (i JoinChatRoom) innan en anslutning läggs till här.
public class RoomService
{
    // För varje anslutning (en flik i webbläsaren): vilka rum den är med i och rummets typ.
    // ConcurrentDictionary: säker när flera anslutningar ändrar samtidigt, t.ex. går med i eller lämnar rum.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, RoomType>> _connections = new();

    // Lägger till rummet för anslutningen. False om den redan var med.
    public bool TryJoin(string connectionId, string roomId, RoomType type) =>
        _connections.GetOrAdd(connectionId, _ => new()).TryAdd(roomId, type);

    // Tar bort rummet för anslutningen. False om den inte var med.
    public bool Leave(string connectionId, string roomId) =>
        _connections.TryGetValue(connectionId, out var rooms) && rooms.TryRemove(roomId, out _);

    // Rumstypen om anslutningen är med i rummet, annars null (= inte behörig).
    // Typen avgör om meddelandet ska vara klartext (lobbyn) eller krypterat (grupprum).
    public RoomType? GetJoinedRoomType(string connectionId, string roomId) => 
        _connections.TryGetValue(connectionId, out var rooms) && rooms.TryGetValue(roomId, out var type)
            ? type: null;

    // Tar bort anslutningen helt (när den stängs) och returnerar rummen den var med i
    public List<string> RemoveConnection(string connectionId) =>
        _connections.TryRemove(connectionId, out var rooms) ? rooms.Keys.ToList() : [];

}
