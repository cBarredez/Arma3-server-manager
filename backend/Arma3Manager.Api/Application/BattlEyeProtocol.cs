using System.IO.Hashing;
using System.Text;
using System.Text.RegularExpressions;
using Arma3Manager.Api.Contracts;

namespace Arma3Manager.Api.Application;

/// <summary>Pure packet framing/parsing for the BattlEye RCon UDP protocol (no socket I/O).</summary>
public static class BattlEyeProtocol
{
    public const byte TypeLogin = 0x00;
    public const byte TypeCommand = 0x01;
    public const byte TypeMessage = 0x02;

    public static byte[] BuildLogin(string password) => Frame(TypeLogin, Encoding.UTF8.GetBytes(password));

    public static byte[] BuildCommand(byte sequence, string command)
    {
        var commandBytes = Encoding.UTF8.GetBytes(command);
        var payload = new byte[1 + commandBytes.Length];
        payload[0] = sequence;
        commandBytes.CopyTo(payload, 1);
        return Frame(TypeCommand, payload);
    }

    public static byte[] BuildMessageAck(byte sequence) => Frame(TypeMessage, [sequence]);

    /// <summary>'BE' + 4-byte CRC32 (over 0xFF+type+payload) + 0xFF + type + payload.</summary>
    static byte[] Frame(byte type, byte[] payload)
    {
        var body = new byte[2 + payload.Length];
        body[0] = 0xFF;
        body[1] = type;
        payload.CopyTo(body, 2);
        var crc = Crc32.Hash(body);
        var packet = new byte[2 + 4 + body.Length];
        packet[0] = (byte)'B';
        packet[1] = (byte)'E';
        crc.CopyTo(packet, 2);
        body.CopyTo(packet, 6);
        return packet;
    }

    /// <summary>Parses an inbound datagram; returns null when the datagram is too short or malformed.</summary>
    public static RconIncomingPacket? TryParse(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < 8 || datagram[0] != (byte)'B' || datagram[1] != (byte)'E' || datagram[6] != 0xFF) return null;
        var type = datagram[7];
        var rest = datagram[8..];
        switch (type)
        {
            case TypeLogin when rest.Length >= 1:
                return new RconLoginResult(rest[0] == 0x01);
            case TypeCommand when rest.Length >= 1:
                var sequence = rest[0];
                var tail = rest[1..];
                if (tail.Length >= 3 && tail[0] == 0x00)
                    return new RconCommandResponse(sequence, true, tail[1], tail[2], tail[3..].ToArray());
                return new RconCommandResponse(sequence, false, 1, 0, tail.ToArray());
            case TypeMessage when rest.Length >= 1:
                return new RconServerMessage(rest[0], Encoding.UTF8.GetString(rest[1..]));
            default:
                return null;
        }
    }
}

public abstract record RconIncomingPacket;
public sealed record RconLoginResult(bool Success) : RconIncomingPacket;
public sealed record RconCommandResponse(byte Sequence, bool IsMultiPart, byte PartTotal, byte PartIndex, byte[] Payload) : RconIncomingPacket;
public sealed record RconServerMessage(byte Sequence, string Text) : RconIncomingPacket;

/// <summary>Reassembles multi-packet BE command responses keyed by sequence number.</summary>
public sealed class RconResponseAssembler
{
    readonly Dictionary<byte, SortedDictionary<byte, byte[]>> pending = [];

    /// <summary>Feeds one response fragment; returns the full decoded text once all parts have arrived, otherwise null.</summary>
    public string? Feed(RconCommandResponse response)
    {
        if (!response.IsMultiPart) return Encoding.UTF8.GetString(response.Payload);
        if (!pending.TryGetValue(response.Sequence, out var parts)) pending[response.Sequence] = parts = [];
        parts[response.PartIndex] = response.Payload;
        if (parts.Count < response.PartTotal) return null;
        pending.Remove(response.Sequence);
        return Encoding.UTF8.GetString(parts.Values.SelectMany(part => part).ToArray());
    }
}

/// <summary>Parses the text response of the BE "players" command into structured rows.</summary>
public static class RconPlayerParser
{
    static readonly Regex Line = new(@"^(?<id>\d+)\s+(?<ip>[\d.]+):(?<port>\d+)\s+(?<ping>-?\d+)\s+(?<guid>[0-9a-f]+)\((?<verified>OK|-|\?)\)\s+(?<name>.+?)\s*(?<lobby>\(Lobby\))?$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    public static IReadOnlyList<RconPlayer> Parse(string playersResponse)
    {
        var players = new List<RconPlayer>();
        foreach (Match match in Line.Matches(playersResponse ?? ""))
        {
            players.Add(new RconPlayer(
                int.Parse(match.Groups["id"].Value),
                match.Groups["guid"].Value,
                match.Groups["name"].Value.Trim(),
                match.Groups["ip"].Value,
                int.Parse(match.Groups["ping"].Value),
                match.Groups["lobby"].Success,
                string.Equals(match.Groups["verified"].Value, "OK", StringComparison.OrdinalIgnoreCase)));
        }
        return players;
    }
}
