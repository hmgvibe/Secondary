using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Secondary.Shared;

public enum InstanceCommand : byte { Show = 1, ToggleGame = 2 }

// One writer for this user's settings profile. IPC carries one bounded command byte.
public sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Mutex ownership;
    private readonly CancellationTokenSource stop = new();
    private Task listener = Task.CompletedTask;
    private readonly string pipeName;
    private bool disposed;
    private string? listenerError;
    public string? LastListenerError => Volatile.Read(ref listenerError);
    private SingleInstanceCoordinator(Mutex ownership, string pipeName)
    { this.ownership = ownership; this.pipeName = pipeName; }

    public static SingleInstanceCoordinator? Acquire(string settingsPath)
    {
        var id = Identity(settingsPath);
        // The same user's profile can be shared by concurrent Windows login sessions.
        // Global named mutexes do not require the global file-mapping creation privilege.
        var mutex = new Mutex(false, "Global\\Secondary-" + id);
        bool acquired;
        try { acquired = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) { mutex.Dispose(); return null; }
        return new(mutex, "Secondary-" + id);
    }
    private static string Identity(string path)
    {
        string key;
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            key = identity.User!.Value + "|" + Path.GetFullPath(path).ToUpperInvariant();
        }
        // Linux paths are case-sensitive; the numeric user id separates accounts.
        else key = "uid:" + getuid() + "|" + Path.GetFullPath(path);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
    }
    [DllImport("libc", EntryPoint = "getuid")] private static extern uint getuid();
    public void Listen(Action show) => Listen(command =>
    {
        if (command != InstanceCommand.Show) return false;
        show();
        return true;
    });
    // The handler only queues UI work and reports whether this instance understands the command.
    public void Listen(Func<InstanceCommand, bool> handle)
    {
        listener = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    deadline.CancelAfter(1000);
                    var command = new byte[1];
                    await pipe.ReadExactlyAsync(command, deadline.Token).ConfigureAwait(false);
                    if (!Enum.IsDefined((InstanceCommand)command[0]) || !handle((InstanceCommand)command[0])) continue;
                    await pipe.WriteAsync(new byte[] { 1 }, deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (IOException error) { await Backoff(error).ConfigureAwait(false); }
                catch (UnauthorizedAccessException error) { await Backoff(error).ConfigureAwait(false); }
                catch (InvalidOperationException) when (stop.IsCancellationRequested) { }
            }
        });
    }
    private async Task Backoff(Exception error)
    {
        if (LastListenerError is null)
            Interlocked.CompareExchange(ref listenerError, error.GetType().Name + " / HResult 0x" + error.HResult.ToString("X8"), null);
        try { await Task.Delay(150, stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    public static Task<bool> ShowExistingAsync(string settingsPath) => SendAsync(settingsPath, InstanceCommand.Show);
    public static async Task<bool> SendAsync(string settingsPath, InstanceCommand command)
    {
        using var deadline = new CancellationTokenSource(3000);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", "Secondary-" + Identity(settingsPath),
                PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            await pipe.WriteAsync(new[] { (byte)command }, deadline.Token).ConfigureAwait(false);
            var acknowledgment = new byte[1];
            await pipe.ReadExactlyAsync(acknowledgment, deadline.Token).ConfigureAwait(false);
            return acknowledgment[0] == 1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException or TimeoutException)
        { return false; }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stop.Cancel();
        // The callback only queues dispatcher work, so this cannot wait on UI execution.
        try { listener.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        ownership.ReleaseMutex(); ownership.Dispose(); stop.Dispose();
    }
}
