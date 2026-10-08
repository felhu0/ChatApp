using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Threading.RateLimiting;

namespace ChatApp.Services;

// Rate limiting: begränsar hur många hubbanrop varje anslutning får göra.
// Skyddar mot spam och överbelastning.
// Körs automatiskt före varje hubbmetod, så att alla metoder skyddas utan extra kod.
public class HubRateLimitFilter(ILogger<HubRateLimitFilter> logger) : IHubFilter
{
    // En "hink" med tokens per anslutning. Concurrent: tål att många anslutningar anropar samtidigt.
    private readonly ConcurrentDictionary<string, TokenBucketRateLimiter> _limiters = new();
    
    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext context, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        // Hämtar anslutningens hink, eller skapar en första gången
        var limiter = _limiters.GetOrAdd(context.Context.ConnectionId, _ =>
            new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = 20,                                // max 20 anrop i en snabb följd
                TokensPerPeriod = 5,                            // fylls på med 5 tokens...                            
                ReplenishmentPeriod = TimeSpan.FromSeconds(5),  // ...var 5:e sekund (ca 1 anrop/s i längden)
                QueueLimit = 0,                                   // inga anrop får vänta i kö: över gränsen nekas direkt med "För många försök"
                AutoReplenishment = true                        // påfyllningen sker automatiskt
            }));

        // Försöker ta en token ur hinken. Varje anrop kostar en.
        using var lease = limiter.AttemptAcquire();
        if (!lease.IsAcquired)
        {
            // Tom hink: logga och neka anropet
            logger.LogWarning("Hub rate limit reached for {UserName} calling {Method}",
                context.Context.User?.Identity?.Name, context.HubMethodName);
            throw new HubException("Too many requests. Please slow down.");
        }

        // Token fanns: kör hubbmetoden som vanligt
        return await next(context);
    }

    // Städa bort hinken när anslutningen stängs, så att minnet inte växer
    public Task OnDisconnectedAsync(
        HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
    {
        if (_limiters.TryRemove(context.Context.ConnectionId, out var limiter))
            limiter.Dispose();

        return next(context, exception);
    }

}
