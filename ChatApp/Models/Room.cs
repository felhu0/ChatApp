namespace ChatApp.Models;

public class Room
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public RoomType Type { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Vilka användare som har behörighet till rummet
    public List<RoomMember> Members { get; set; } = [];
}
