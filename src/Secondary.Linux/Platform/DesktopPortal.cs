using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Secondary.Shared;
using Tmds.DBus.Protocol;

namespace Secondary.Linux.Platform;

public enum ShortcutState { Unsupported, Waiting, Bound, Declined, NotAllowed, Failed }
public sealed record ShortcutResult(ShortcutState State, string Detail);

// Session-bus access to the XDG desktop portal and the tray watcher. Every call is optional:
// a missing portal, an old portal version or a declined request leaves the app usable.
public sealed class DesktopPortal : IDisposable
{
    public const string ApplicationId = "io.github.hmgvibe.Secondary";
    public const string ToggleGameShortcut = "toggle-game";
    private const string PortalBus = "org.freedesktop.portal.Desktop";
    private const string PortalPath = "/org/freedesktop/portal/desktop";
    private readonly DBusConnection connection;
    private readonly List<IDisposable> watches = new();
    private string? shortcutSession;
    private IDisposable? activatedWatch;
    // Null when the portal accepted the application ID; otherwise the D-Bus error name.
    public string? RegistrationError { get; private set; }
    private DesktopPortal(DBusConnection connection) => this.connection = connection;

    public static async Task<DesktopPortal?> ConnectAsync()
    {
        if (!OperatingSystem.IsLinux() || DBusAddress.Session is null) return null;
        var connection = new DBusConnection(DBusAddress.Session);
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);
            var portal = new DesktopPortal(connection);
            await portal.RegisterAsync().ConfigureAwait(false);
            return portal;
        }
        catch (Exception error) when (error is DBusExceptionBase or System.IO.IOException)
        { AppDiagnostics.Write("dbus-connect", error); connection.Dispose(); return null; }
    }

    private delegate void Body(ref MessageWriter writer);
    // MessageWriter cannot live across an await, so each call is built synchronously first.
    private MessageBuffer Call(string destination, string path, string @interface, string member, string signature, Body? body)
    {
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination, path, @interface, member, signature.Length == 0 ? null : signature, MessageFlags.None);
            body?.Invoke(ref writer);
            return writer.CreateMessage();
        }
        finally { writer.Dispose(); }
    }
    private MessageBuffer PortalCall(string @interface, string member, string signature, Body? body) => Call(PortalBus, PortalPath, @interface, member, signature, body);

    // Unsandboxed apps have no portal identity unless they register one before any other portal call.
    // Older portals lack the registry; they then identify the app on their own.
    private async Task RegisterAsync()
    {
        try
        {
            await connection.CallMethodAsync(PortalCall("org.freedesktop.host.portal.Registry", "Register", "sa{sv}", (ref MessageWriter w) =>
            { w.WriteString(ApplicationId); w.WriteDictionary(new Dictionary<string, VariantValue>()); })).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException error) { RegistrationError = error.ErrorName; }
    }

    public async Task<bool> HasTrayWatcherAsync()
    {
        try
        {
            return await connection.CallMethodAsync(Call("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "NameHasOwner", "s",
                (ref MessageWriter w) => w.WriteString("org.kde.StatusNotifierWatcher")), (m, _) => m.GetBodyReader().ReadBool(), null).ConfigureAwait(false);
        }
        catch (DBusExceptionBase error) { AppDiagnostics.Write("tray-watcher", error); return false; }
    }

    public async Task<bool?> ReadBoolAsync(string @namespace, string key)
    {
        var value = await ReadSettingAsync(@namespace, key).ConfigureAwait(false);
        return value is { Type: VariantValueType.Bool } v ? v.GetBool() : null;
    }

    public async Task<string?> ReadStringAsync(string @namespace, string key)
    {
        var value = await ReadSettingAsync(@namespace, key).ConfigureAwait(false);
        return value is { Type: VariantValueType.String } v ? v.GetString() : null;
    }

    private async Task<VariantValue?> ReadSettingAsync(string @namespace, string key)
    {
        foreach (var member in new[] { "ReadOne", "Read" })
        {
            try
            {
                var value = await connection.CallMethodAsync(PortalCall("org.freedesktop.portal.Settings", member, "ss", (ref MessageWriter w) =>
                { w.WriteString(@namespace); w.WriteString(key); }), (m, _) => m.GetBodyReader().ReadVariantValue(), null).ConfigureAwait(false);
                // The deprecated Read wraps the value in a second variant.
                while (value.Type == VariantValueType.Variant) value = value.GetVariantValue();
                return value;
            }
            catch (DBusErrorReplyException error) when (member == "ReadOne" && error.ErrorName == "org.freedesktop.DBus.Error.UnknownMethod") { }
            catch (DBusExceptionBase) { return null; }
        }
        return null;
    }

    public async Task WatchSettingsAsync(Action<string, string> changed)
    {
        try
        {
            watches.Add(await connection.WatchSignalAsync(PortalBus, PortalPath, "org.freedesktop.portal.Settings", "SettingChanged",
                (m, _) => { var r = m.GetBodyReader(); return (r.ReadString(), r.ReadString()); },
                (Notification<(string, string)> n) => { if (n.HasValue) changed(n.Value.Item1, n.Value.Item2); },
                ObserverFlags.None, false, null).ConfigureAwait(false));
        }
        catch (DBusExceptionBase error) { AppDiagnostics.Write("portal-settings-watch", error); }
    }

    // Asks GNOME (or another portal backend) for a global shortcut. The desktop may show its own
    // confirmation; the user's choice there is final and is never bypassed.
    public async Task<ShortcutResult> BindToggleGameAsync(string preferredTrigger, Action activated, CancellationToken cancellation)
    {
        try
        {
            if (!await HasGlobalShortcutsAsync().ConfigureAwait(false))
                return new(ShortcutState.Unsupported, "此桌面的入口服務不提供全域快捷鍵。");
            await CloseShortcutSessionAsync().ConfigureAwait(false);
            var sessionToken = Token();
            var created = await RequestAsync("CreateSession", "a{sv}", token => (ref MessageWriter w) =>
                w.WriteDictionary(new Dictionary<string, VariantValue> { ["handle_token"] = VariantValue.String(token), ["session_handle_token"] = VariantValue.String(sessionToken) }),
                TimeSpan.FromSeconds(10), cancellation).ConfigureAwait(false);
            if (created.Code != 0 || !created.Results.TryGetValue("session_handle", out var handle))
                return new(ShortcutState.Failed, "無法建立快捷鍵工作階段。");
            var session = handle.Type == VariantValueType.ObjectPath ? handle.GetObjectPathAsString() : handle.GetString();
            shortcutSession = session;
            activatedWatch = await connection.WatchSignalAsync(PortalBus, PortalPath, "org.freedesktop.portal.GlobalShortcuts", "Activated",
                (m, _) => { var r = m.GetBodyReader(); return (r.ReadObjectPathAsString(), r.ReadString()); },
                (Notification<(string, string)> n) => { if (n.HasValue && n.Value.Item1 == session && n.Value.Item2 == ToggleGameShortcut) activated(); },
                ObserverFlags.None, false, null).ConfigureAwait(false);
            // Binding can wait on a desktop dialog, so allow the user time to answer.
            var bound = await RequestAsync("BindShortcuts", "oa(sa{sv})sa{sv}", token => (ref MessageWriter w) =>
            {
                w.WriteObjectPath(session);
                var shortcuts = w.WriteArrayStart(DBusType.Struct);
                w.WriteStructureStart();
                w.WriteString(ToggleGameShortcut);
                w.WriteDictionary(new Dictionary<string, VariantValue> { ["description"] = VariantValue.String("切換 Secondary 遊戲模式"), ["preferred_trigger"] = VariantValue.String(preferredTrigger) });
                w.WriteArrayEnd(shortcuts);
                w.WriteString("");
                w.WriteDictionary(new Dictionary<string, VariantValue> { ["handle_token"] = VariantValue.String(token) });
            }, TimeSpan.FromMinutes(3), cancellation).ConfigureAwait(false);
            if (bound.Code == 1) return new(ShortcutState.Declined, "已在桌面對話框取消；可重新套用。");
            if (bound.Code != 0) return new(ShortcutState.Failed, "桌面未完成快捷鍵綁定。");
            return new(ShortcutState.Bound, Describe(bound.Results) ?? "已由桌面綁定。");
        }
        catch (TimeoutException) { return new(ShortcutState.Failed, "等待桌面回應逾時；可重新套用。"); }
        catch (OperationCanceledException) { return new(ShortcutState.Failed, "已停止。"); }
        catch (DBusErrorReplyException error) when (error.ErrorName == "org.freedesktop.portal.Error.NotAllowed")
        { return new(ShortcutState.NotAllowed, "桌面不允許這個程式綁定全域快捷鍵。"); }
        catch (DBusExceptionBase error)
        {
            AppDiagnostics.Write("global-shortcuts", error);
            // The D-Bus error name identifies the refusal without carrying user data.
            var name = error is DBusErrorReplyException reply ? "（" + reply.ErrorName + "）" : "";
            return new(ShortcutState.Failed, "入口服務回報錯誤" + name + "，請改用自訂快捷鍵指令。");
        }
    }

    private static string? Describe(Dictionary<string, VariantValue> results)
    {
        if (!results.TryGetValue("shortcuts", out var list) || list.Type != VariantValueType.Array) return null;
        for (var i = 0; i < list.Count; i++)
        {
            var entry = list.GetItem(i);
            if (entry.GetItem(0).GetString() != ToggleGameShortcut) continue;
            var properties = entry.GetItem(1);
            for (var j = 0; j < properties.Count; j++)
            {
                var pair = properties.GetDictionaryEntry(j);
                if (pair.Key.GetString() != "trigger_description") continue;
                var value = pair.Value;
                while (value.Type == VariantValueType.Variant) value = value.GetVariantValue();
                return "已由桌面綁定：" + value.GetString();
            }
        }
        return null;
    }

    private async Task<bool> HasGlobalShortcutsAsync()
    {
        try
        {
            var version = await connection.CallMethodAsync(PortalCall("org.freedesktop.DBus.Properties", "Get", "ss", (ref MessageWriter w) =>
            { w.WriteString("org.freedesktop.portal.GlobalShortcuts"); w.WriteString("version"); }),
                (m, _) => m.GetBodyReader().ReadVariantValue(), null).ConfigureAwait(false);
            while (version.Type == VariantValueType.Variant) version = version.GetVariantValue();
            return version.Type == VariantValueType.UInt32 && version.GetUInt32() >= 1;
        }
        catch (DBusErrorReplyException) { return false; }
    }

    private sealed record PortalResponse(uint Code, Dictionary<string, VariantValue> Results);

    // Portal requests answer through a Response signal on a predictable path; subscribe before calling.
    private async Task<PortalResponse> RequestAsync(string member, string signature, Func<string, Body> body, TimeSpan timeout, CancellationToken cancellation)
    {
        var token = Token();
        var sender = connection.UniqueName!.TrimStart(':').Replace('.', '_');
        var response = new TaskCompletionSource<PortalResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = await connection.WatchSignalAsync(PortalBus, $"{PortalPath}/request/{sender}/{token}", "org.freedesktop.portal.Request", "Response",
            (m, _) => { var r = m.GetBodyReader(); return new PortalResponse(r.ReadUInt32(), r.ReadDictionaryOfStringToVariantValue()); },
            (Notification<PortalResponse> n) =>
            {
                if (n.HasValue) response.TrySetResult(n.Value);
                else if (n.Exception is { } error) response.TrySetException(error);
            }, ObserverFlags.None, false, null).ConfigureAwait(false);
        await connection.CallMethodAsync(PortalCall("org.freedesktop.portal.GlobalShortcuts", member, signature, body(token))).ConfigureAwait(false);
        return await response.Task.WaitAsync(timeout, cancellation).ConfigureAwait(false);
    }

    private static string Token() => "secondary_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    private async Task CloseShortcutSessionAsync()
    {
        activatedWatch?.Dispose();
        activatedWatch = null;
        if (shortcutSession is null) return;
        var session = shortcutSession;
        shortcutSession = null;
        try { await connection.CallMethodAsync(Call(PortalBus, session, "org.freedesktop.portal.Session", "Close", "", null)).ConfigureAwait(false); }
        catch (DBusExceptionBase) { }
    }

    public void Dispose()
    {
        activatedWatch?.Dispose();
        foreach (var watch in watches) watch.Dispose();
        watches.Clear();
        connection.Dispose();
    }
}
