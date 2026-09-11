using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ParkNest.Api.Realtime;

/// <summary>
/// The live channel: session timers, overstay warnings, wallet movements, notifications.
///
/// Authenticated like everything else. SignalR reads the same bearer token, with one wrinkle —
/// browsers cannot set headers on a WebSocket handshake, so the token also arrives as a query
/// parameter for this path only (see the JWT options in <c>Program</c>).
///
/// The hub itself has no methods worth calling. Everything flows outward: clients subscribe by
/// connecting, and the server pushes. A client that could invoke server methods here would be a
/// second, weaker way to reach things the controllers already guard properly.
/// </summary>
[Authorize]
public sealed class ParkNestHub : Hub
{
    /// <summary>
    /// Everything is addressed to a user rather than a connection, because a person is routinely
    /// two connections — the phone in their hand and a browser tab left open — and a wallet update
    /// that reached only one of them would be worse than none.
    /// </summary>
    public static string UserGroup(Guid userId) => $"user:{userId}";

    public override async Task OnConnectedAsync()
    {
        var userId = Context.UserIdentifier;

        if (!string.IsNullOrEmpty(userId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{userId}");
        }

        await base.OnConnectedAsync();
    }
}
