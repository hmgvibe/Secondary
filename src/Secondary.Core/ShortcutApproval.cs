using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Secondary.Core;

public static class ShortcutApproval
{
    // Metadata-only edits keep approval; a new ID, reordered/changed steps or
    // changed script hash in the execution review always requires confirmation.
    public static string Fingerprint(ShortcutEntry entry, string review) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { entry.Id, entry.Steps, Review = review }))));
    public static bool IsApproved(ShortcutEntry entry, string review) => entry.ApprovedFingerprint == Fingerprint(entry,review);
}
