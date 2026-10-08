using ChatApp.Data;
using ChatApp.Models;
using ChatApp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using System.Security.Cryptography;


namespace ChatApp.Hubs;

// Hubben tar emot anrop från webbläsarna och skickar meddelanden vidare till rätt rum i realtid.
// Servern sköter behörighet, validering och lagring – men kan aldrig läsa nycklar eller grupprumsmeddelanden.
// Bara anslutningar med en giltig JWT (JSON Web Token) släpps in.
[Authorize]
public class ChatHub : Hub<IChatClient>
{
    private const int MaxMessageLength = 500;
    private const int MaxRoomIdLength = 64;
    private const int MaxRoomNameLength = 50;
    private const int P256PublicKeyLength = 65; // publik nyckel: 0x04 + X (32 bytes) + Y (32 bytes)
    private const int WrappedKeyLength = 48; // inslagen rumsnyckel: 32 bytes rumsnyckel + 16 bytes tagg
    private const int IvLength = 12;         // IV (Initialization Vector)
    private const int GcmTagLength = 16; // kontrollsumman (tagg) från GCM (Galois/Counter Mode)
    private const int MinCiphertextLength = 1 + GcmTagLength;  // minst ett tecken + tagg

    // Max: upp till 4 bytes per tecken i UTF-8 (Unicode Transformation Format – 8 bit), plus taggen
    private const int MaxCiphertextLength = MaxMessageLength * 4 + GcmTagLength;
    private const int HistoryCount = 50; // antal meddelanden som laddas per rum

    private readonly RoomService _rooms;
    private readonly AppDbContext _db;
    private readonly ILogger<ChatHub> _logger;

    public ChatHub(RoomService rooms, AppDbContext db, ILogger<ChatHub> logger)
    {
        _rooms = rooms;
        _db = db;
        _logger = logger;
    }

    // ----- Nycklar -----

    // Sparar användarens publika nyckel (öppna delen av enhetsnyckeln).
    // Valideras först, så att bara giltiga punkter på kurvan P-256 sparas.
    public async Task SetPublicKey(string publicKey)
    {
        var raw = DecodePublicKey(publicKey);

        var userId = GetUserId();
        var user = await _db.Users.SingleAsync(u => u.Id == userId);
        user.PublicKey = Convert.ToBase64String(raw);
        await _db.SaveChangesAsync();
    }

    // Hämtar en användares publika nyckel (öppna delen av enhetsnyckeln),
    // så att admin kan slå in rumsnyckeln åt den som bjuds in. Null om användaren aldrig loggat in.
    public async Task<string?> GetPublicKey(string userName)
    {
        var normalized = userName?.Trim().ToUpperInvariant() ?? "";
        return await _db.Users
            .Where(u => u.NormalizedUserName == normalized)
            .Select(u => u.PublicKey)
            .SingleOrDefaultAsync();
    }

    // ----- Rum -----

    // Returnerar lobbyn plus de grupprum användaren är medlem i,
    // med användarens egen inslagna rumsnyckel (rumsnyckeln krypterad åt just den här användaren)
    public async Task<List<RoomDto>> GetMyRooms()
    {
        var userId = GetUserId();

        var rooms = await _db.Rooms
            .Where(r => r.Type == RoomType.Public || r.Members.Any(m => m.UserId == userId)) 
            .OrderBy(r => r.Name)
            .Select(r => new
            {
                r.Id,
                r.Name,
                r.Type,
                // Bara den egna medlemsraden – andras inslagna rumsnycklar skickas aldrig ut
                Me = r.Members
                    .Where(m => m.UserId == userId)
                    .Select(m => new {m.IsAdmin, m.WrappedKey, m.KeyIv, m.EphemeralPublicKey })
                    .FirstOrDefault()
            })
            .ToListAsync();

        return rooms.Select(r => new RoomDto(
             r.Id, 
             r.Name, 
             r.Type.ToString().ToLowerInvariant(), 
             r.Me?.IsAdmin ?? false,
             r.Me?.WrappedKey is null ? null // lobbyn har ingen rumsnyckel
                : new WrappedKeyDto(r.Me.WrappedKey, r.Me.KeyIv!, r.Me.EphemeralPublicKey!)))
            .ToList();
    }


    // Skapar ett grupprum. Servern väljer rums-id och gör skaparen till admin.
    // Rumsnyckeln (nyckel som skaparen av ett grupprum slumpar fram) skapas i webbläsaren –
    // servern får bara den inslagna rumsnyckeln (rumsnyckeln krypterad åt skaparen).
    public async Task CreateGroupRoom(string name, WrappedKeyDto key)
    {
        name = name?.Trim() ?? "";
        if (name.Length == 0 || name.Length > MaxRoomNameLength || name.Any(char.IsControl))
            throw new HubException($"Room name must be 1–{MaxRoomNameLength} characters.");

        ValidateWrappedKey(key);

        var userId = GetUserId();
        var room = new Room
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            Type = RoomType.Group
        };
        room.Members.Add(new RoomMember 
        { 
            RoomId = room.Id, 
            UserId = userId, 
            IsAdmin = true,
            WrappedKey = key.WrappedKey,
            KeyIv = key.KeyIv,
            EphemeralPublicKey = key.EphemeralPublicKey
        });

        _db.Rooms.Add(room);
        await _db.SaveChangesAsync();

        // Skickas till alla skaparens flikar, som sedan låser upp rumsnyckeln och går med i rummet
        await Clients.User(userId.ToString())
            .RoomAdded(new RoomDto(room.Id, room.Name, "group", true, key));

        _logger.LogInformation("User {UserName} created room {RoomId}", GetUserName(), room.Id);
    }

    // Bjuder in en användare till ett grupprum. Bara admin får göra det.
    // Admin har redan slagit in rumsnyckeln åt den inbjudna i webbläsaren – servern sparar den bara.
    public async Task InviteUser(string roomId, string userName, WrappedKeyDto key)
    {
        var userId = GetUserId();

        ValidateWrappedKey(key);

        // Admin-kontroll mot databasen. Samma fel om rummet inte finns, så att inget avslöjas.
        var room = await _db.Rooms.AsNoTracking().SingleOrDefaultAsync(r =>
            r.Id == roomId && r.Type == RoomType.Group &&
            r.Members.Any(m => m.UserId == userId && m.IsAdmin))
            ?? throw new HubException("You are not allowed to invite to this room.");

        var normalized = userName?.Trim().ToUpperInvariant() ?? "";
        var invitee = await _db.Users.AsNoTracking()
            .SingleOrDefaultAsync(u => u.NormalizedUserName == normalized)
            ?? throw new HubException("User not found.");

        // Utan publik nyckel (öppna delen av enhetsnyckeln) kan ingen rumsnyckel ha slagits in åt användaren
        if (invitee.PublicKey is null)
            throw new HubException($"{invitee.UserName} has to log in once before being invited.");

        if (await _db.RoomMembers.AnyAsync(m => m.RoomId == room.Id && m.UserId == invitee.Id))
            throw new HubException($"{invitee.UserName} is already a member.");

        // Den inbjudnas inslagna rumsnyckel sparas på den nya medlemsraden
        _db.RoomMembers.Add(new RoomMember 
        { 
            RoomId = room.Id, 
            UserId = invitee.Id,
            WrappedKey = key.WrappedKey,
            KeyIv = key.KeyIv,
            EphemeralPublicKey = key.EphemeralPublicKey
        });
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Unika nyckeln (RoomId + UserId) stoppade en dubblett
            throw new HubException($"{invitee.UserName} is already a member.");
        }

        // Den inbjudna får rummet och sin inslagna rumsnyckel direkt i alla sina flikar
        await Clients.User(invitee.Id.ToString())
            .RoomAdded(new RoomDto(room.Id, room.Name, "group", false, key));

        await Clients.Group(room.Id)
            .ReceiveSystemMessage(room.Id, $"{GetUserName()} invited {invitee.UserName}");

        _logger.LogInformation("User {UserName} invited {Invitee} to room {RoomId}",
            GetUserName(), invitee.UserName, room.Id);
    }

    // Hämtar rummets chattlogg (de senaste meddelandena), äldst först.
    // Behörigheten kontrolleras mot databasen.
    public async Task<List<MessageDto>> GetHistory(string roomId)
    {
        var room = await GetRoomIfAllowed(roomId, GetUserId());

        var messages = await _db.Messages
            .AsNoTracking()
            .Where(m => m.RoomId == room.Id)
            .OrderByDescending(m => m.Id) // de senaste
            .Take(HistoryCount)
            .Select(m => new { m.Sender.UserName, m.Content, m.Ciphertext, m.Iv, m.SentAt })
            .ToListAsync();

        messages.Reverse(); // men visas äldst först

        // Lobbyn: Content. Grupprum: Encrypted, som låses upp med rumsnyckeln i webbläsaren.
        return messages.Select(m => new MessageDto(
            m.UserName,
            m.Content,
            m.Ciphertext is null ? null : new EncryptedMessageDto(m.Ciphertext, m.Iv!),
            m.SentAt)).ToList();
    }

    // Går med i ett rum efter behörighetskontroll mot databasen.
    // Rummet och dess typ sparas sedan i minnet (RoomService) för snabba kontroller.
    public async Task JoinChatRoom(string roomId)
    {
        var userName = GetUserName();
        var room = await GetRoomIfAllowed(roomId, GetUserId());

        if (!_rooms.TryJoin(Context.ConnectionId, room.Id, room.Type))
            return;  // redan med i rummet

        await Groups.AddToGroupAsync(Context.ConnectionId, room.Id);
        await Clients.Group(room.Id).ReceiveSystemMessage(room.Id, $"{userName} has joined {room.Name}");
    }

    public async Task LeaveChatRoom(string roomId)
    {
        if (!_rooms.Leave(Context.ConnectionId, roomId))
            return; // var inte med i rummet

        await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId);
        await Clients.Group(roomId).ReceiveSystemMessage(roomId, $"{GetUserName()} has left");
    }

    // ----- Meddelanden -----

    // Klartextmeddelande – bara i lobbyn, som skyddas av TLS (Transport Layer Security)
    public async Task SendMessage(string roomId, string message)
    {
        message = message?.Trim() ?? "";
        if (message.Length == 0 || message.Length > MaxMessageLength)
            throw new HubException($"Message must be 1–{MaxMessageLength} characters.");

        var type = _rooms.GetJoinedRoomType(Context.ConnectionId, roomId)
            ?? throw new HubException("You are not a member of this room.");

        // Klartext får aldrig skickas till ett privat rum, även om klienten har en bugg
        if (type != RoomType.Public)
            throw new HubException("This room requires encrypted messages.");

        // Sparas före utskicket, så att ingen ser ett meddelande som inte finns i chattloggen
        _db.Messages.Add(new ChatMessage { RoomId = roomId, SenderId = GetUserId(), Content = message });
        await _db.SaveChangesAsync();

        await Clients.Group(roomId).ReceiveMessage(roomId, GetUserName(), message);
    }

    // Krypterat meddelande – bara i grupprum.
    // Låst med rumsnyckeln i webbläsaren.
    // Servern kontrollerar bara formatet och kan inte läsa innehållet.
    public async Task SendEncryptedMessage(string roomId, EncryptedMessageDto message)
    {
        if (message is null || DecodedLength(message.Iv) != IvLength)
            throw new HubException("Invalid encrypted message.");

        var length = DecodedLength(message.Ciphertext);
        if (length < MinCiphertextLength || length > MaxCiphertextLength)
            throw new HubException("Invalid encrypted message.");

        var type = _rooms.GetJoinedRoomType(Context.ConnectionId, roomId)
            ?? throw new HubException("You are not a member of this room.");

        if (type == RoomType.Public)
            throw new HubException("Public rooms do not use end-to-end encryption.");

        // Bara ciphertext och IV (Initialization Vector) sparas – servern har aldrig rumsnyckeln
        _db.Messages.Add(new ChatMessage
        {
            RoomId = roomId,
            SenderId = GetUserId(),
            Ciphertext = message.Ciphertext,
            Iv = message.Iv,
        });
        await _db.SaveChangesAsync();

        // Avsändaren sätts av servern från JWT (JSON Web Token), aldrig från klienten
        await Clients.Group(roomId).ReceiveEncryptedMessage(roomId, GetUserName(), message);
    }

    // ----- Anslutning -----

    // Körs automatiskt när en anslutning öppnas
    public override Task OnConnectedAsync()
    {
        _logger.LogInformation("User {UserName} connected", GetUserName());
        return base.OnConnectedAsync();
    }

    // Körs automatiskt när en anslutning stängs
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userName = GetUserName();

        // SignalR tar själv bort anslutningen ur grupperna, vi meddelar bara de andra
        foreach (var roomId in _rooms.RemoveConnection(Context.ConnectionId))
            await Clients.Group(roomId).ReceiveSystemMessage(roomId, $"{userName} has left");

        _logger.LogInformation("User {UserName} disconnected", userName);

        await base.OnDisconnectedAsync(exception);
    }

    // ----- Hjälpmetoder -----

    // Hämtar rummet om användaren har behörighet: lobbyn för alla, grupprum bara för medlemmar
    private async Task<Room> GetRoomIfAllowed(string roomId, int userId)
    {
        if (string.IsNullOrEmpty(roomId) || roomId.Length > MaxRoomIdLength)
            throw new HubException("You do not have access to this room.");

        var room = await _db.Rooms
            .AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == roomId &&
                (r.Type == RoomType.Public || r.Members.Any(m => m.UserId == userId)));

        if (room is null)
        {
            // Rums-id kommer från klienten och rensas innan det loggas (skydd mot log injection)
            _logger.LogWarning("User {UserName} was denied access to room {RoomId}",
                GetUserName(), LogSanitizer.Clean(roomId, MaxRoomIdLength));
            throw new HubException("You do not have access to this room.");
        }
        return room;        
    }

    // Kontrollerar att en publik nyckel är giltig: rätt längd, rätt format och en punkt på kurvan P-256 (den matematiska kurva som ECDH, Elliptic Curve Diffie-Hellman, räknar på).
    // Skydd mot att en angripare laddar upp en punkt utanför kurvan (invalid curve attack).
    private static byte[] DecodePublicKey(string? publicKey)
    {
        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(publicKey ?? "");
        }
        catch (FormatException)
        {
            throw new HubException("Invalid public key.");
        }
        // Kollar att nyckeln är exakt 65 bytes och börjar med 0x04 (betyder att både x och y finns med).
        // Annars är det ingen riktig publik nyckel, och den nekas.
        if (raw.Length != P256PublicKeyLength || raw[0] != 0x04) 
            throw new HubException("Invalid public key.");

        try
        {
            // Importen misslyckas om punkten inte ligger på kurvan
            using var ecdh = ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,                  // kurvan P-256, samma som i webbläsaren
                Q = new ECPoint { X = raw[1..33], Y = raw[33..] }       // den publika nyckeln som punkt (x, y)
            });
        }
        catch (CryptographicException)
        {
            throw new HubException("Invalid public key.");
        }

        return raw;
    }

    // Kontrollerar formatet på en inslagen rumsnyckel (rumsnyckeln krypterad åt en viss medlem).
    // Innehållet kan servern inte kontrollera – det är krypterat.
    private static void ValidateWrappedKey(WrappedKeyDto? key)
    {
        if (key is null ||
            DecodedLength(key.WrappedKey) != WrappedKeyLength ||
            DecodedLength(key.KeyIv) != IvLength)
            throw new HubException("Invalid room key.");

        DecodePublicKey(key.EphemeralPublicKey); // den tillfälliga publika nyckeln måste också vara giltig
    }

    // Antal bytes efter Base64-avkodning, eller -1 om strängen inte är giltig Base64
    private static int DecodedLength(string? b64)
    {
        try { return Convert.FromBase64String(b64 ?? "").Length; }
        catch (FormatException) { return -1; }
    }

    // Användarens id ("sub") från den signerade JWT, aldrig från klienten
    private int GetUserId() =>
        int.TryParse(Context.User?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value, out var id)
            ? id
            : throw new HubException("Not authenticated.");

    // Användarnamnet från den signerade JWT, aldrig från klienten
    private string GetUserName() =>
        Context.User?.Identity?.Name
        ?? throw new HubException("Not authenticated.");
    
}