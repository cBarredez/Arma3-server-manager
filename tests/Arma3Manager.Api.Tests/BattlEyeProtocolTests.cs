using System.IO.Hashing;
using System.Text;
using Arma3Manager.Api.Application;
using Arma3Manager.Api.Contracts;
using Xunit;

namespace Arma3Manager.Api.Tests;

public sealed class BattlEyeProtocolTests
{
    [Fact]
    public void Crc32MatchesTheStandardCheckValue()
    {
        // Canonical CRC-32/ISO-HDLC (zlib) check value for the ASCII string "123456789".
        var crc = Crc32.Hash(Encoding.ASCII.GetBytes("123456789"));
        Assert.Equal(new byte[] { 0x26, 0x39, 0xF4, 0xCB }, crc);
    }

    [Fact]
    public void BuildLoginProducesAWellFramedPacket()
    {
        var packet = BattlEyeProtocol.BuildLogin("secret");
        Assert.Equal((byte)'B', packet[0]);
        Assert.Equal((byte)'E', packet[1]);
        Assert.Equal(0xFF, packet[6]);
        Assert.Equal(BattlEyeProtocol.TypeLogin, packet[7]);
        Assert.Equal("secret", Encoding.UTF8.GetString(packet[8..]));

        var body = packet[6..];
        Assert.Equal(Crc32.Hash(body), packet[2..6]);
    }

    [Fact]
    public void BuildCommandEmbedsSequenceAndCommandText()
    {
        var packet = BattlEyeProtocol.BuildCommand(42, "players");
        Assert.Equal(BattlEyeProtocol.TypeCommand, packet[7]);
        Assert.Equal(42, packet[8]);
        Assert.Equal("players", Encoding.UTF8.GetString(packet[9..]));
    }

    [Fact]
    public void BuildCommandWrapsSequenceByteAt256()
    {
        byte sequence = 255;
        sequence++;
        var packet = BattlEyeProtocol.BuildCommand(sequence, "");
        Assert.Equal(0, packet[8]);
    }

    [Fact]
    public void TryParseRoundTripsLoginSuccess()
    {
        var packet = BuildIncoming(BattlEyeProtocol.TypeLogin, [0x01]);
        var parsed = BattlEyeProtocol.TryParse(packet);
        Assert.Equal(new RconLoginResult(true), parsed);
    }

    [Fact]
    public void TryParseRoundTripsLoginFailure()
    {
        var packet = BuildIncoming(BattlEyeProtocol.TypeLogin, [0x00]);
        var parsed = BattlEyeProtocol.TryParse(packet);
        Assert.Equal(new RconLoginResult(false), parsed);
    }

    [Fact]
    public void TryParseRoundTripsASinglePartCommandResponse()
    {
        var payload = new byte[] { 5 }.Concat(Encoding.UTF8.GetBytes("OK")).ToArray();
        var packet = BuildIncoming(BattlEyeProtocol.TypeCommand, payload);
        var parsed = Assert.IsType<RconCommandResponse>(BattlEyeProtocol.TryParse(packet));
        Assert.Equal(5, parsed.Sequence);
        Assert.False(parsed.IsMultiPart);
        Assert.Equal("OK", Encoding.UTF8.GetString(parsed.Payload));
    }

    [Fact]
    public void TryParseRoundTripsAMultiPartCommandResponse()
    {
        var payload = new byte[] { 7, 0x00, 2, 1 }.Concat(Encoding.UTF8.GetBytes("second")).ToArray();
        var packet = BuildIncoming(BattlEyeProtocol.TypeCommand, payload);
        var parsed = Assert.IsType<RconCommandResponse>(BattlEyeProtocol.TryParse(packet));
        Assert.Equal(7, parsed.Sequence);
        Assert.True(parsed.IsMultiPart);
        Assert.Equal(2, parsed.PartTotal);
        Assert.Equal(1, parsed.PartIndex);
        Assert.Equal("second", Encoding.UTF8.GetString(parsed.Payload));
    }

    [Fact]
    public void TryParseRoundTripsAServerMessage()
    {
        var payload = new byte[] { 3 }.Concat(Encoding.UTF8.GetBytes("Player connected")).ToArray();
        var packet = BuildIncoming(BattlEyeProtocol.TypeMessage, payload);
        var parsed = Assert.IsType<RconServerMessage>(BattlEyeProtocol.TryParse(packet));
        Assert.Equal(3, parsed.Sequence);
        Assert.Equal("Player connected", parsed.Text);
    }

    [Fact]
    public void TryParseRejectsTooShortDatagrams()
    {
        Assert.Null(BattlEyeProtocol.TryParse(new byte[] { (byte)'B', (byte)'E', 0, 0 }));
    }

    [Fact]
    public void ResponseAssemblerReassemblesOutOfOrderParts()
    {
        var assembler = new RconResponseAssembler();
        var part1 = new RconCommandResponse(1, true, 2, 1, Encoding.UTF8.GetBytes("world"));
        var part0 = new RconCommandResponse(1, true, 2, 0, Encoding.UTF8.GetBytes("hello "));

        Assert.Null(assembler.Feed(part1));
        Assert.Equal("hello world", assembler.Feed(part0));
    }

    [Fact]
    public void ResponseAssemblerPassesThroughSinglePartResponses()
    {
        var assembler = new RconResponseAssembler();
        var response = new RconCommandResponse(9, false, 1, 0, Encoding.UTF8.GetBytes("done"));
        Assert.Equal("done", assembler.Feed(response));
    }

    [Fact]
    public void PlayerParserExtractsStructuredRows()
    {
        const string sample =
            "Players on server:\n" +
            "[#] [IP Address]:[Port] [Ping] [GUID] [Name]\n" +
            "--------------------------------------------------\n" +
            "0   192.168.1.10:2304     45   abcdef0123456789abcdef0123456789(OK) Alpha\n" +
            "1   192.168.1.11:2305     80   0123456789abcdef0123456789abcdef(?) Bravo (Lobby)\n" +
            "(2 players in total)\n";

        var players = RconPlayerParser.Parse(sample);

        Assert.Equal(2, players.Count);
        Assert.Equal(0, players[0].Id);
        Assert.Equal("Alpha", players[0].Name);
        Assert.Equal("192.168.1.10", players[0].Ip);
        Assert.Equal(45, players[0].Ping);
        Assert.True(players[0].Verified);
        Assert.False(players[0].Lobby);

        Assert.Equal(1, players[1].Id);
        Assert.Equal("Bravo", players[1].Name);
        Assert.False(players[1].Verified);
        Assert.True(players[1].Lobby);
    }

    static byte[] BuildIncoming(byte type, byte[] payload)
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
}
