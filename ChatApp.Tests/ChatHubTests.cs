using System.Security.Claims;
using ChatApp.Data;
using ChatApp.Hubs;
using ChatApp.Models;
using ChatApp.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ChatApp.Tests;

// Testar meddelanden, behörighet och felhantering i hubben.
// SignalR-delarna mockas med Moq (låtsasobjekt som spelar in vad hubben gör),
// databasen är en riktig SQLite i minnet.
// IDisposable: databasen stängs efter varje test.
public class ChatHubTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly RoomService _rooms = new();
    private readonly Mock<IChatClient> _groupClient = new(); // låtsasmottagare: fångar det hubben skickar till ett rum

    // Körs före varje test: ny, tom databas med användaren "anna"
    public ChatHubTests()
    {
        // Databasen finns bara så länge anslutningen är öppen
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated(); // skapar tabellerna och lobbyn (HasData)

        _db.Users.Add(new User { Id = 1, UserName = "anna", NormalizedUserName = "ANNA", PasswordHash = "x" });
        _db.SaveChanges();
    }

    // Körs efter varje test: stänger databasen, så att den försvinner
    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // Skapar en hubb som om "anna" (id 1) var inloggad med en giltig JWT (JSON Web Token).
    // Claims "sub" och "name" är samma som en riktig token innehåller.
    private ChatHub CreateHub()
    {
        var identity = new ClaimsIdentity(
            [new Claim("sub", "1"), new Claim("name", "anna")],
            authenticationType: "Test", nameType: "name", roleType: null);

        // Låtsasanslutning: inloggad användare och ett anslutnings-id
        var context = new Mock<HubCallerContext>();
        context.Setup(c => c.User).Returns(new ClaimsPrincipal(identity));
        context.Setup(c => c.ConnectionId).Returns("conn-1");

        // Allt som skickas till ett rum hamnar hos _groupClient, så att testet kan kontrollera det
        var clients = new Mock<IHubCallerClients<IChatClient>>();
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(_groupClient.Object);

        return new ChatHub(_rooms, _db, NullLogger<ChatHub>.Instance)
        {
            Context = context.Object,
            Clients = clients.Object,
            Groups = Mock.Of<IGroupManager>()
        };
    }

    // Lobbymeddelande sparas i chattloggen och skickas ut.
    // Avsändaren sätts av servern från token, aldrig från klienten.
    [Fact]
    public async Task SendMessage_InLobby_IsSavedAndBroadcastWithServerSideSender()
    {
        // Arrange – anna har gått med i lobbyn
        _rooms.TryJoin("conn-1", "lobby", RoomType.Public);
        var hub = CreateHub();

        // Act 
        await hub.SendMessage("lobby", "  hej  ");

        // Assert – avsändaren kommer från token (inte från klienten) och texten trimmas
        _groupClient.Verify(c => c.ReceiveMessage("lobby", "anna", "hej"), Times.Once);

        // Assert – meddelandet sparades i chattloggen
        var saved = await _db.Messages.SingleAsync();
        Assert.Equal("hej", saved.Content);
        Assert.Equal(1, saved.SenderId);
    }

    // Validering: för långa meddelanden nekas, och inget skickas eller sparas
    [Fact]
    public async Task SendMessage_TooLong_ThrowsAndSendsNothing()
    {
        _rooms.TryJoin("conn-1", "lobby", RoomType.Public);
        var hub = CreateHub();

        // Act + Assert – 501 tecken är ett för mycket
        await Assert.ThrowsAsync<HubException>(() => hub.SendMessage("lobby", new string('x', 501)));

        // Assert – inget skickades och inget sparades
        _groupClient.Verify(c => c.ReceiveMessage(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        Assert.Empty(_db.Messages);
    }

    // Behörighet: man kan inte skicka till ett rum man inte gått med i
    [Fact]
    public async Task SendMessage_NotJoinedRoom_Throws()
    {
        // Arrange – anna har inte gått med i något rum
        var hub = CreateHub(); 

        var ex = await Assert.ThrowsAsync<HubException>(() => hub.SendMessage("lobby", "hej"));
        Assert.Equal("You are not a member of this room.", ex.Message);
    }

    // Kryptering: klartext får aldrig skickas till ett grupprum, även om klienten försöker
    [Fact]
    public async Task SendMessage_PlaintextToGroupRoom_Throws()
    {
        // Arrange – anna är med i ett grupprum
        _rooms.TryJoin("conn-1", "group-1", RoomType.Group);
        var hub = CreateHub();

        var ex = await Assert.ThrowsAsync<HubException>(() => hub.SendMessage("group-1", "hemligt"));
        Assert.Equal("This room requires encrypted messages.", ex.Message);
    }

    // Behörighet: man kan inte gå med i ett grupprum utan att vara medlem i databasen
    [Fact]
    public async Task JoinChatRoom_PrivateRoomWithoutMembership_IsDenied()
    {
        // Arrange – ett grupprum där anna INTE är medlem
        _db.Rooms.Add(new Room { Id = "secret", Name = "Hemligt", Type = RoomType.Group });
        await _db.SaveChangesAsync();
        var hub = CreateHub();

        // Act + Assert – nekas
        await Assert.ThrowsAsync<HubException>(() => hub.JoinChatRoom("secret"));

        // Assert – rummet lades inte till i minnet
        Assert.Null(_rooms.GetJoinedRoomType("conn-1", "secret"));
    }
}