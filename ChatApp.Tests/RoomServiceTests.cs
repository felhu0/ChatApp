using ChatApp.Models;
using ChatApp.Services;

namespace ChatApp.Tests;

// Testar RoomService: vilka rum varje anslutning (flik) är med i, i minnet.
// Täcker anslutning till rum och frånkoppling.
public class RoomServiceTests
{
    // Samma anslutning kan inte gå med i samma rum två gånger
    [Fact]
    public void TryJoin_FirstTime_ReturnsTrue_SecondTime_ReturnsFalse()
    {
        // Arrange
        var rooms = new RoomService();

        // Act – går med två gånger i samma rum
        var first = rooms.TryJoin("conn-1", "lobby", RoomType.Public);
        var second = rooms.TryJoin("conn-1", "lobby", RoomType.Public);

        // Assert –  första gången lyckas, andra gången inte
        Assert.True(first);
        Assert.False(second);
    }

    // Rumstypen finns bara för rum som anslutningen faktiskt gått med i.
    // Det är den här kontrollen hubben använder för att neka meddelanden till fel rum.
    [Fact]
    public void GetJoinedRoomType_ReturnsType_OnlyForJoinedRooms()
    {
        // Arrange – conn-1 är med i room-a
        var rooms = new RoomService();
        rooms.TryJoin("conn-1", "room-a", RoomType.Group);

        Assert.Equal(RoomType.Group, rooms.GetJoinedRoomType("conn-1", "room-a"));// med i rummet
        Assert.Null(rooms.GetJoinedRoomType("conn-1", "room-b"));  // inte med i rummet
        Assert.Null(rooms.GetJoinedRoomType("conn-2", "room-a"));  // annan anslutning
    }

    // När en anslutning stängs tas den bort helt, och alla dess rum returneras
    [Fact]
    public void RemoveConnection_ReturnsAllRooms_AndForgetsConnection()
    {
        // Arrange – conn-1 är med i två rum
        var rooms = new RoomService();
        rooms.TryJoin("conn-1", "lobby", RoomType.Public);
        rooms.TryJoin("conn-1", "room-a", RoomType.Group);

        // Act – som när användaren stänger fliken
        var left = rooms.RemoveConnection("conn-1");

        // Assert – båda rummen returneras (för "has left"-meddelanden)
        Assert.Equal(2, left.Count);
        Assert.Contains("lobby", left);
        Assert.Contains("room-a", left);

        // Assert – anslutningen är glömd
        Assert.Null(rooms.GetJoinedRoomType("conn-1", "lobby"));
    }

    // Att lämna ett rum man inte är med i ger false, inget fel
    [Fact]
    public void Leave_RoomNotJoined_ReturnsFalse()
    {
        // Arrange
        var rooms = new RoomService();

        // Act + Assert
        Assert.False(rooms.Leave("conn-1", "lobby"));
    }
}