using ChatApp.Models;
using Microsoft.EntityFrameworkCore;
using System.Runtime.ConstrainedExecution;

namespace ChatApp.Data;

// Kopplingen mellan C#-klasserna och databasen (SQLite).
public class AppDbContext(DbContextOptions<AppDbContext> option) : DbContext(option) 
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Room> Rooms => Set<Room>();
    public DbSet<RoomMember> RoomMembers => Set<RoomMember>();
    public DbSet<ChatMessage> Messages => Set<ChatMessage>();

    // Regler för tabellerna: längder, nycklar, index och relationer
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            // Maxlängder skyddar mot för stora värden i databasen
            e.Property(u => u.UserName).HasMaxLength(30);
            e.Property(u => u.NormalizedUserName).HasMaxLength(30);
            e.Property(u => u.PublicKey).HasMaxLength(100); // publik ECDH (Elliptic Curve Diffie-Hellman)-nyckel, 88 tecken i Base64

            // Unikt index: två användare kan inte heta samma sak, oavsett versaler
            e.HasIndex(u => u.NormalizedUserName).IsUnique();
        });

        // Lobbyn skapas av migreringen och finns alltid.
        modelBuilder.Entity<Room>(e =>
        {
            e.Property(r => r.Id).HasMaxLength(64);
            e.Property(r => r.Name).HasMaxLength(50);

            e.Property(r => r.Type).HasConversion<string>().HasMaxLength(10);

            e.HasData(new Room
            {
                Id = "lobby",
                Name = "Lobby",
                Type = RoomType.Public,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            });
        });

        modelBuilder.Entity<RoomMember>(e =>
        {
            // En användare kan bara vara medlem en gång per rum
            e.HasKey(m => new { m.RoomId, m.UserId });

            // Tas ett rum eller en användare bort försvinner medlemskapen också
            e.HasOne(m => m.Room).WithMany(r => r.Members)
                .HasForeignKey(m => m.RoomId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(m => m.User).WithMany()
                .HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);

            // Snabb sökning på "vilka rum är användaren med i?"
            e.HasIndex(m => m.UserId);

            // Den inslagna rumsnyckeln (ECIES (Elliptic Curve Integrated Encryption Scheme)).
            // Servern lagrar den men kan inte låsa upp den.
            e.Property(m => m.WrappedKey).HasMaxLength(100);  // rumsnyckel + tagg = 48 bytes = 64 tecken
            e.Property(m => m.KeyIv).HasMaxLength(100);        // IV (Initialization Vector) = 12 bytes = 16 tecken
            e.Property(m => m.EphemeralPublicKey).HasMaxLength(100);  // tillfällig publik nyckel = 65 bytes = 88 tecken
        });

        modelBuilder.Entity<ChatMessage>(e =>
        {
            e.Property(m => m.Content).HasMaxLength(500);    // klartext, bara för lobbyn
            e.Property(m => m.Ciphertext).HasMaxLength(3000); // krypterat, för grupprum (ca 2 kB som Base64)
            e.Property(m => m.Iv).HasMaxLength(24);     // IV (Initialization Vector) för meddelandet

            // Tas rummet eller användaren bort försvinner meddelandena också
            e.HasOne(m => m.Room).WithMany()
                .HasForeignKey(m => m.RoomId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(m => m.Sender).WithMany()
                .HasForeignKey(m => m.SenderId).OnDelete(DeleteBehavior.Cascade);

            // Snabb hämtning av "senaste meddelandena i rum X" (chattloggar)
            e.HasIndex(m => new { m.RoomId, m.Id });
        });
    }
}
