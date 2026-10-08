using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.JsonWebTokens;

namespace ChatApp.Services;

// Talar om för SignalR vilken användare en anslutning tillhör.
public class SubUserIdProvider : IUserIdProvider
{
    // "sub" (subject) = användarens id i token
    public string? GetUserId(HubConnectionContext connection) =>
        connection.User?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
}
