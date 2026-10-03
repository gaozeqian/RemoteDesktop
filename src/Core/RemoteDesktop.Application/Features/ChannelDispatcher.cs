using RemoteDesktop.Protocol;
using RemoteDesktop.Transport;

namespace RemoteDesktop.Application.Features;

public enum DispatchResult
{
    Accepted,
    NoHandler,
    Denied,
}

/// <summary>
/// Routes channels opened by the peer to the matching feature module. On the host this is where permissions
/// (including watch mode) are enforced: a channel the grant does not cover is closed before any module sees it.
/// </summary>
public sealed class ChannelDispatcher
{
    private readonly Dictionary<ChannelType, IFeatureModule> _modules;

    public ChannelDispatcher(IEnumerable<IFeatureModule> modules)
    {
        ArgumentNullException.ThrowIfNull(modules);

        _modules = [];
        foreach (var module in modules)
        {
            if (!_modules.TryAdd(module.ChannelType, module))
            {
                throw new InvalidOperationException($"More than one feature module handles channel {module.ChannelType}.");
            }
        }
    }

    public DispatchResult Authorize(ChannelType type, SessionContext session, out IFeatureModule? module)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!_modules.TryGetValue(type, out module))
        {
            return DispatchResult.NoHandler;
        }

        if (session.Role == SessionRole.Host && !session.Grant.Allows(module.RequiredPermission))
        {
            module = null;
            return DispatchResult.Denied;
        }

        return DispatchResult.Accepted;
    }

    /// <summary>Authorizes and serves a channel; rejected channels are disposed.</summary>
    public async Task<DispatchResult> DispatchAsync(IChannel channel, SessionContext session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var result = Authorize(channel.Type, session, out var module);
        if (result != DispatchResult.Accepted)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            return result;
        }

        await using (channel.ConfigureAwait(false))
        {
            await module!.HandleAsync(channel, session, cancellationToken).ConfigureAwait(false);
        }

        return DispatchResult.Accepted;
    }
}
