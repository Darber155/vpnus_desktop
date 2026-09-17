using VpnUs.Core.Clash;

namespace VpnUs.App.Services;

/// <summary>
/// Считает накопительный трафик отдельно по VPN-соединениям (chains содержит proxy/ноду)
/// и по всей системе. Clash API отдаёт только активные соединения, поэтому накопление
/// делается по дельтам каждого connection id.
/// </summary>
public sealed class VpnTrafficTracker
{
    private readonly Dictionary<string, (long Upload, long Download)> _known = new(StringComparer.Ordinal);

    public long ProxyUpload { get; private set; }

    public long ProxyDownload { get; private set; }

    public long SystemUpload { get; private set; }

    public long SystemDownload { get; private set; }

    public void Update(IReadOnlyList<ClashConnection> connections)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var connection in connections)
        {
            if (connection.Id.Length == 0)
            {
                continue;
            }

            seen.Add(connection.Id);
            var isProxy = connection.IsProxy;

            long deltaUp;
            long deltaDown;

            if (_known.TryGetValue(connection.Id, out var previous))
            {
                deltaUp = Math.Max(0, connection.Upload - previous.Upload);
                deltaDown = Math.Max(0, connection.Download - previous.Download);
            }
            else
            {
                deltaUp = connection.Upload;
                deltaDown = connection.Download;
            }

            SystemUpload += deltaUp;
            SystemDownload += deltaDown;

            if (isProxy)
            {
                ProxyUpload += deltaUp;
                ProxyDownload += deltaDown;
            }

            _known[connection.Id] = (connection.Upload, connection.Download);
        }

        if (_known.Count > seen.Count)
        {
            foreach (var id in _known.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                _known.Remove(id);
            }
        }
    }

    public void Reset()
    {
        _known.Clear();
        ProxyUpload = 0;
        ProxyDownload = 0;
        SystemUpload = 0;
        SystemDownload = 0;
    }
}
