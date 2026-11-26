using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Secondary.Core;
using Secondary.Desktop;
using Secondary.Shared;
using Secondary.Shared.Connection;
using static Secondary.Shared.Connection.ConnectionProtocol;

namespace Secondary.Connect;

// What the main computer's user is asked before a shortcut runs for the first time or after it changed.
public sealed record ShortcutConfirmation(string Device, string Name, string Details);

// Reviews a shortcut (with script hashes) and runs its steps; the WPF ShortcutExecutor in the app, a fake in tests.
public interface IReviewingShortcutExecutor : IShortcutExecutor { string Review(ShortcutEntry entry); }
public sealed class WindowsReviewingExecutor : IReviewingShortcutExecutor
{
    private readonly ShortcutExecutor executor = new();
    public string Review(ShortcutEntry entry) => executor.Review(entry);
    public Task Execute(ShortcutStep step, CancellationToken cancellation) => executor.Execute(step, cancellation);
}

// The main computer's shortcuts, kept by the helper under Windows rules. Devices can change them, but approval
// stays here: a device can never mark a shortcut approved, and the first run or a changed one waits for this
// computer's user. Same review, hash check and step runner as the WPF launchpad.
public sealed class ShortcutService
{
    private readonly ShortcutLibrary library;
    private readonly IReviewingShortcutExecutor executor;
    private readonly ShortcutRunner runner;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> runs = new();
    private readonly object gate = new();
    private ShortcutEntry[] entries;
    private string? running;
    public event Action? Changed;
    // Set by the tray: shows the confirmation on this computer's screen. Without it nothing runs unapproved.
    public Func<ShortcutConfirmation, CancellationToken, Task<bool>>? Confirm { get; set; }
    // While the device shows its game page, shortcuts do not run, as on the device itself.
    public Func<bool> Busy { get; set; } = () => false;
    public string? Error => library.Error;
    public ShortcutEntry[] Entries { get { lock (gate) return entries; } }
    public string Status => running is { } name ? $"快捷方式：正在执行「{name}」" : $"主机快捷方式：{Entries.Length} 个";

    public ShortcutService(string path, IReviewingShortcutExecutor? executor = null)
    {
        library = new ShortcutLibrary(path, ShortcutRules.Windows);
        entries = library.Load();
        this.executor = executor ?? new WindowsReviewingExecutor();
        runner = new ShortcutRunner(this.executor);
    }

    public Task Handle(string device, JsonObject message, FrameChannel channel, CancellationToken session)
    {
        switch (TypeOf(message))
        {
            case "shortcuts":
                ShortcutEntry[] current;
                lock (gate) current = entries;
                return Send(channel, Reply(message, RemoteShortcuts.List(current, current.Where(e => e.ApprovedFingerprint is not null).Select(e => e.Id), library.Error)), session);
            case "shortcut-save": return Answer(message, channel, Save(message["entry"]), session);
            case "shortcut-delete": return Answer(message, channel, Change(Text(message, "shortcut"), list => list.Where(e => e.Id != Text(message, "shortcut")).ToArray()), session);
            case "shortcut-reset": return Answer(message, channel, Change(Text(message, "shortcut"), list => list.Select(e => e.Id == Text(message, "shortcut") ? e with { ApprovedFingerprint = null } : e).ToArray()), session);
            case "shortcut-run" when Text(message, "shortcut") is { } id && Text(message, "run") is { Length: > 0 and <= 64 } run:
                _ = Task.Run(() => Run(device, id, run, message, channel, session));
                return Task.CompletedTask;
            case "shortcut-cancel" when Text(message, "run") is { } cancel:
                if (runs.TryGetValue(cancel, out var owner)) try { owner.Cancel(); } catch (ObjectDisposedException) { }
                return Task.CompletedTask;
            default: return Task.CompletedTask;
        }
    }

    // A device's copy never carries approval; an edit keeps this computer's approval, which the fingerprint
    // invalidates by itself when the steps or script contents changed.
    private string? Save(JsonNode? node)
    {
        ShortcutEntry? incoming;
        try { incoming = node?.Deserialize<ShortcutEntry>(); }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or ArgumentException) { incoming = null; }
        if (incoming is null) return "快捷方式内容无法读取。";
        if (ShortcutPolicy.Validate(incoming, ShortcutRules.Windows) is { } problem) return problem;
        lock (gate)
        {
            var existing = entries.FirstOrDefault(e => e.Id == incoming.Id);
            var entry = incoming with { ApprovedFingerprint = existing?.ApprovedFingerprint };
            if (existing is null && entries.Length >= RemoteShortcuts.MaxEntries) return "主机已有 200 个快捷方式，请先删除不用的。";
            var next = existing is null ? entries.Append(entry).ToArray() : entries.Select(e => e.Id == entry.Id ? entry : e).ToArray();
            return Store(next);
        }
    }
    private string? Change(string? id, Func<ShortcutEntry[], ShortcutEntry[]> change)
    {
        lock (gate)
        {
            if (id is null || entries.All(e => e.Id != id)) return "主机上找不到这个快捷方式。";
            return Store(change(entries));
        }
    }
    private string? Store(ShortcutEntry[] next)
    {
        if (!library.Save(next)) return "主机保存失败：" + library.Error;
        entries = next;
        Changed?.Invoke();
        return null;
    }

    private async Task Run(string device, string id, string run, JsonObject request, FrameChannel channel, CancellationToken session)
    {
        using var owner = CancellationTokenSource.CreateLinkedTokenSource(session);
        var token = owner.Token;
        async Task Progress(string text)
        {
            try { await channel.SendAsync(Message("shortcut-status", ("run", run), ("text", text)), token).ConfigureAwait(false); }
            catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { }
        }
        async Task Finish(bool ok, string text) => await Send(channel, Reply(request, Message("shortcut-result", ("ok", ok), ("text", text))), session).ConfigureAwait(false);
        if (Busy()) { await Finish(false, "设备正在游戏模式，主机不执行快捷方式。").ConfigureAwait(false); return; }
        ShortcutEntry? entry;
        lock (gate)
        {
            entry = entries.FirstOrDefault(e => e.Id == id);
            if (entry is not null && running is null) running = entry.Name;
            else if (entry is not null) entry = null;
        }
        if (entry is null) { await Finish(false, running is null ? "主机上找不到这个快捷方式；请重新整理。" : "主机已有快捷方式正在执行。").ConfigureAwait(false); return; }
        runs[run] = owner;
        Changed?.Invoke();
        try
        {
            await Progress("正在读取主机上的执行内容…").ConfigureAwait(false);
            var details = await Task.Run(() => executor.Review(entry), token).ConfigureAwait(false);
            var fingerprint = ShortcutApproval.Fingerprint(entry, details);
            if (entry.ApprovedFingerprint != fingerprint)
            {
                await Progress("等待主机确认：请到主机上确认或取消。").ConfigureAwait(false);
                var confirmed = Confirm is { } confirm && await confirm(new ShortcutConfirmation(device, entry.Name, details), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!confirmed) { await Finish(false, "主机上已取消或超时，未执行。").ConfigureAwait(false); return; }
                lock (gate)
                {
                    var current = entries.FirstOrDefault(e => e.Id == id);
                    if (current is null || !current.Steps!.SequenceEqual(entry.Steps!)) throw new InvalidOperationException("快捷方式在确认期间已变更，这次未执行；请重新执行并确认。");
                    entry = current with { ApprovedFingerprint = fingerprint };
                    if (Store(entries.Select(e => e.Id == id ? entry : e).ToArray()) is { } problem) throw new IOException(problem);
                }
            }
            var steps = entry.Steps!.Length;
            await runner.Run(entry, step => _ = Progress($"在主机执行「{entry.Name}」· 步骤 {step}/{steps}"), token).ConfigureAwait(false);
            await Finish(true, "已在主机完成：" + entry.Name).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { await Finish(false, "已取消；不会关闭已开启的程序，也不能撤销已完成的操作。").ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            AppDiagnostics.Write("connect-shortcut", error);
            await Finish(false, "执行停止：" + error.Message).ConfigureAwait(false);
        }
        finally
        {
            runs.TryRemove(run, out _);
            lock (gate) running = null;
            Changed?.Invoke();
        }
    }

    private static Task Answer(JsonObject request, FrameChannel channel, string? error, CancellationToken session) =>
        Send(channel, Reply(request, Message("shortcut-reply", ("ok", error is null), ("error", error))), session);
    private static async Task Send(FrameChannel channel, JsonObject message, CancellationToken session)
    {
        try { await channel.SendAsync(message, session).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { }
    }

    // Stops whatever is running when the helper exits.
    public void CancelAll() { foreach (var owner in runs.Values) try { owner.Cancel(); } catch (ObjectDisposedException) { } }
}
