using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Secondary.Core;
using static Secondary.Shared.Connection.ConnectionProtocol;

namespace Secondary.Shared.Connection;

// The main computer's shortcuts as a device sees them. Approval never leaves the main computer:
// Approved only says which entries will run without asking there.
public sealed record RemoteShortcutList(ShortcutEntry[] Entries, IReadOnlySet<string> Approved, string? Error);

// Shortcuts stored on the main computer, under Windows rules. A device may list, add, edit, delete and ask to run them;
// the main computer validates everything again and asks its own user before anything runs for the first time.
public static class RemoteShortcuts
{
    public const int MaxEntries = 200;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    public static JsonNode Entry(ShortcutEntry entry) => JsonSerializer.SerializeToNode(entry with { ApprovedFingerprint = null })!;
    // Entries from the other side are checked against the Windows rules; anything that fails is dropped.
    public static ShortcutEntry? ReadEntry(JsonNode? node)
    {
        if (node is not JsonObject) return null;
        try
        {
            var entry = node.Deserialize<ShortcutEntry>();
            return entry is not null && ShortcutPolicy.Validate(entry, ShortcutRules.Windows) is null ? entry with { ApprovedFingerprint = null } : null;
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or ArgumentException or NullReferenceException) { return null; }
    }

    public static JsonObject List(IEnumerable<ShortcutEntry> entries, IEnumerable<string> approved, string? error) => Message("shortcut-list",
        ("entries", new JsonArray(entries.Take(MaxEntries).Select(Entry).ToArray())), ("approved", new JsonArray(approved.Select(id => (JsonNode)id).ToArray())), ("error", error));
    public static RemoteShortcutList ReadList(JsonObject reply)
    {
        var entries = (reply["entries"] as JsonArray ?? []).Take(MaxEntries).Select(ReadEntry).OfType<ShortcutEntry>().DistinctBy(e => e.Id).ToArray();
        var approved = (reply["approved"] as JsonArray ?? []).Select(n => n?.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : null).OfType<string>().ToHashSet();
        return new(entries, approved, Text(reply, "error"));
    }

    public static async Task<RemoteShortcutList> ListAsync(ConnectionController controller, CancellationToken token = default) =>
        ReadList(await controller.RequestAsync(Message("shortcuts"), Wait, token).ConfigureAwait(false));
    // Each change returns null on success, or why the main computer refused it.
    public static Task<string?> SaveAsync(ConnectionController controller, ShortcutEntry entry, CancellationToken token = default) =>
        Change(controller, Message("shortcut-save", ("entry", Entry(entry))), token);
    public static Task<string?> DeleteAsync(ConnectionController controller, string id, CancellationToken token = default) =>
        Change(controller, Message("shortcut-delete", ("shortcut", id)), token);
    public static Task<string?> ResetApprovalAsync(ConnectionController controller, string id, CancellationToken token = default) =>
        Change(controller, Message("shortcut-reset", ("shortcut", id)), token);
    private static async Task<string?> Change(ConnectionController controller, JsonObject message, CancellationToken token)
    {
        var reply = await controller.RequestAsync(message, Wait, token).ConfigureAwait(false);
        return reply["ok"]?.GetValueKind() == JsonValueKind.True ? null : Text(reply, "error") ?? "主電腦拒絕了這項變更。";
    }

    // Runs one shortcut on the main computer. Progress arrives as text (waiting for confirmation there, step n/m);
    // cancelling stops it there. A lost connection also stops it, because the helper cancels the session's work.
    public static async Task<(bool Ok, string Text)> RunAsync(ConnectionController controller, string id, Action<string> progress, CancellationToken token = default)
    {
        var run = Guid.NewGuid().ToString("N");
        void OnMessage(JsonObject message)
        {
            if (TypeOf(message) == "shortcut-status" && Text(message, "run") == run && Text(message, "text") is { Length: <= 500 } text) progress(text);
        }
        controller.MessageReceived += OnMessage;
        using var cancel = token.Register(() => _ = controller.SendAsync(Message("shortcut-cancel", ("run", run))).ContinueWith(_ => { }, TaskScheduler.Default));
        try
        {
            var reply = await controller.RequestAsync(Message("shortcut-run", ("shortcut", id), ("run", run)), Timeout.InfiniteTimeSpan).ConfigureAwait(false);
            return (reply["ok"]?.GetValueKind() == JsonValueKind.True, Text(reply, "text") ?? "");
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        { return (false, "與主電腦的連線中斷；主電腦上的捷徑已停止。"); }
        finally { controller.MessageReceived -= OnMessage; }
    }
}
