using System.Net;
using System.Net.Sockets;

namespace RepoSyncRadar.Core.Services.Preview;

public interface IPreviewPortAllocator
{
    int AllocateSingle(int preferredPort, IReadOnlyCollection<int> reusablePorts);

    PreviewPortPair AllocateComparison(int preferredAfterPort, IReadOnlyCollection<int> reusablePorts);
}

public readonly record struct PreviewPortPair(int AfterPort, int BeforePort);

public sealed class TcpPreviewPortAllocator : IPreviewPortAllocator
{
    public int AllocateSingle(int preferredPort, IReadOnlyCollection<int> reusablePorts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(preferredPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(preferredPort, 65535);
        ArgumentNullException.ThrowIfNull(reusablePorts);

        for (var port = preferredPort; port <= 65535; port++)
        {
            if (IsUsable(port, reusablePorts))
            {
                return port;
            }
        }

        throw new InvalidOperationException(
            $"PreviewBasePort {preferredPort} 以降に利用可能な localhost port がありません。");
    }

    public PreviewPortPair AllocateComparison(int preferredAfterPort, IReadOnlyCollection<int> reusablePorts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(preferredAfterPort, 1);
        if (preferredAfterPort >= 65535)
        {
            throw new InvalidOperationException("PreviewBasePort は比較プレビュー用に連続 2 port を使うため、65534 以下にしてください。");
        }
        ArgumentNullException.ThrowIfNull(reusablePorts);

        for (var afterPort = preferredAfterPort; afterPort < 65535; afterPort++)
        {
            var beforePort = afterPort + 1;
            if (IsUsable(afterPort, reusablePorts)
                && IsUsable(beforePort, reusablePorts))
            {
                return new PreviewPortPair(afterPort, beforePort);
            }
        }

        throw new InvalidOperationException(
            $"PreviewBasePort {preferredAfterPort} 以降に比較用の連続 localhost port がありません。");
    }

    private static bool IsUsable(int port, IReadOnlyCollection<int> reusablePorts)
    {
        if (reusablePorts.Contains(port))
        {
            return true;
        }

        using var listener = new TcpListener(IPAddress.Loopback, port)
        {
            ExclusiveAddressUse = true,
        };
        try
        {
            listener.Start();
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        {
            return false;
        }
    }
}