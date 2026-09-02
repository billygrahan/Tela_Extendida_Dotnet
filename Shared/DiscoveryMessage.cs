using System.Text;

namespace Shared;

public class DiscoveryMessage
{
    public string ServerName { get; set; } = string.Empty;
    public int TcpPort { get; set; } = Constants.StreamPort;

    public byte[] Serialize()
    {
        string payload = $"{Constants.DiscoveryMagicHeader}|{ServerName}|{TcpPort}";
        return Encoding.UTF8.GetBytes(payload);
    }

    public static DiscoveryMessage? Deserialize(byte[] data)
    {
        string payload = Encoding.UTF8.GetString(data);
        string[] parts = payload.Split('|');

        if (parts.Length == 3 && parts[0] == Constants.DiscoveryMagicHeader)
        {
            return new DiscoveryMessage
            {
                ServerName = parts[1],
                TcpPort = int.Parse(parts[2])
            };
        }

        return null;
    }
}