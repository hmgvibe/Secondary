using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using static Secondary.Shared.Connection.ConnectionProtocol;

namespace Secondary.Shared.Connection;

// Passkey entry as in Bluetooth Secure Simple Pairing: the 6-digit code is proven one bit per round
// with commit-then-reveal, and every commitment covers both certificate fingerprints as each side sees them.
// Someone in the middle must guess each bit before seeing it, so success is about 1 in a million per attempt,
// and nothing sent lets the code be brute-forced offline.
public static class Pairing
{
    public const int Bits = 20;
    public const int CodeLimit = 1_000_000;
    private const int NonceBytes = 16;

    public static string NewCode() => RandomNumberGenerator.GetInt32(0, CodeLimit).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    public static bool TryParse(string? text, out int code)
    {
        code = 0;
        var digits = new string((text ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        return digits.Length == 6 && digits.All(char.IsAsciiDigit) && int.TryParse(digits, out code);
    }

    internal static byte[] Commit(byte[] nonce, char role, string own, string peer, int bit) =>
        HMACSHA256.HashData(nonce, Encoding.ASCII.GetBytes($"secondary-pair-v1|{role}|{own}|{peer}|{bit}"));

    // The device commits first in every round. Returns false, after telling the other side, when a bit does not match.
    public static async Task<bool> RunAsync(FrameChannel channel, bool device, int code, string own, string peer, CancellationToken token)
    {
        var (mine, theirs) = device ? ('D', 'H') : ('H', 'D');
        for (var round = 0; round < Bits; round++)
        {
            var bit = (code >> round) & 1;
            var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
            var commitment = Commit(nonce, mine, own, peer, bit);
            byte[] theirCommitment;
            if (device)
            {
                await channel.SendAsync(Message("pair-commit", ("round", round), ("value", Convert.ToBase64String(commitment))), token).ConfigureAwait(false);
                theirCommitment = await Expect(channel, "pair-commit", round, token).ConfigureAwait(false);
                await channel.SendAsync(Message("pair-reveal", ("round", round), ("value", Convert.ToBase64String(nonce))), token).ConfigureAwait(false);
                var revealed = await Expect(channel, "pair-reveal", round, token).ConfigureAwait(false);
                if (!Matches(theirCommitment, Commit(revealed, theirs, peer, own, bit))) return await Fail(channel, token).ConfigureAwait(false);
            }
            else
            {
                theirCommitment = await Expect(channel, "pair-commit", round, token).ConfigureAwait(false);
                await channel.SendAsync(Message("pair-commit", ("round", round), ("value", Convert.ToBase64String(commitment))), token).ConfigureAwait(false);
                var revealed = await Expect(channel, "pair-reveal", round, token).ConfigureAwait(false);
                // A wrong bit stops here, before this side reveals anything about its own bit.
                if (!Matches(theirCommitment, Commit(revealed, theirs, peer, own, bit))) return await Fail(channel, token).ConfigureAwait(false);
                await channel.SendAsync(Message("pair-reveal", ("round", round), ("value", Convert.ToBase64String(nonce))), token).ConfigureAwait(false);
            }
        }
        return true;
    }

    private static bool Matches(byte[] expected, byte[] actual) => CryptographicOperations.FixedTimeEquals(expected, actual);
    private static async Task<bool> Fail(FrameChannel channel, CancellationToken token)
    {
        await channel.SendAsync(Message("error", ("code", "pair-failed"), ("message", "配對碼不符")), token).ConfigureAwait(false);
        return false;
    }
    private static async Task<byte[]> Expect(FrameChannel channel, string type, int round, CancellationToken token)
    {
        var message = await channel.ReceiveAsync(HandshakeTimeout, token).ConfigureAwait(false);
        if (TypeOf(message) == "error") throw new ConnectionRefusedException(Text(message, "code") ?? "error", Text(message, "message") ?? "對方拒絕配對");
        if (TypeOf(message) != type || Number(message, "round") != round || Text(message, "value") is not { } value)
            throw new System.IO.InvalidDataException("配對訊息順序錯誤");
        var bytes = Convert.FromBase64String(value);
        if (bytes.Length != (type == "pair-commit" ? 32 : NonceBytes)) throw new System.IO.InvalidDataException("配對訊息長度錯誤");
        return bytes;
    }
}
