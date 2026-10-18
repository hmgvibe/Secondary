using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Secondary.Shared.Connection;

// Address and port are where a helper was last reached; AutoConnect marks the helper a device reconnects to at start.
public sealed record PairedPeer(string Fingerprint, string Name, DateTimeOffset PairedAt,
    string? Address = null, int Port = 0, DateTimeOffset? LastSeen = null, bool AutoConnect = false);

// The other side's fingerprints this machine trusts. Saved atomically; an unreadable file is kept aside, not overwritten.
public sealed class PairedStore
{
    public const int MaxPeers = 8;
    private const int MaxBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, MaxDepth = 8 };
    private readonly object gate = new();
    private readonly string path;
    private List<PairedPeer> peers = [];
    public event Action? Changed;
    public string? LoadError { get; }
    public string? SaveError { get; private set; }

    public PairedStore(string path)
    {
        this.path = path;
        if (!File.Exists(path)) return;
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxBytes) throw new InvalidDataException("配對檔案過大");
            peers = (JsonSerializer.Deserialize<List<PairedPeer>>(File.ReadAllBytes(path), Options) ?? [])
                .Where(p => p is { Fingerprint.Length: 64, Name.Length: > 0 }).DistinctBy(p => p.Fingerprint).Take(MaxPeers).ToList();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            LoadError = "配對資料無法讀取，已另存並重新開始：" + error.Message;
            try { File.Move(path, path + "." + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + ".corrupt", true); }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException) { }
        }
    }

    public IReadOnlyList<PairedPeer> Peers { get { lock (gate) return peers.ToArray(); } }
    public PairedPeer? Find(string fingerprint) { lock (gate) return peers.FirstOrDefault(p => p.Fingerprint == fingerprint); }

    // Adding a known fingerprint replaces it; a ninth new peer is refused.
    public bool Save(PairedPeer peer)
    {
        lock (gate)
        {
            var index = peers.FindIndex(p => p.Fingerprint == peer.Fingerprint);
            if (index < 0 && peers.Count >= MaxPeers) return false;
            if (index < 0) peers.Add(peer); else peers[index] = peer;
            Write();
        }
        Changed?.Invoke();
        return true;
    }
    public bool Remove(string fingerprint)
    {
        lock (gate)
        {
            if (peers.RemoveAll(p => p.Fingerprint == fingerprint) == 0) return false;
            Write();
        }
        Changed?.Invoke();
        return true;
    }
    // Only one helper is reconnected to automatically.
    public void SetAutoConnect(string? fingerprint)
    {
        lock (gate)
        {
            peers = peers.Select(p => p with { AutoConnect = p.Fingerprint == fingerprint }).ToList();
            Write();
        }
        Changed?.Invoke();
    }

    private void Write()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(peers, Options));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, true);
            SaveError = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { SaveError = "配對資料儲存失敗：" + error.Message; }
    }
}
