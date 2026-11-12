using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Sim.Net;

/// <summary>A line-based TCP connection (one JSON message per line).</summary>
public sealed class TcpLineConnection : IPeer, IDisposable
{
    private readonly TcpClient _client;
    private readonly StreamWriter _writer;
    private readonly object _writeLock = new();
    private volatile bool _closed;

    public string Address { get; }

    private TcpLineConnection(TcpClient client)
    {
        _client = client;
        client.NoDelay = true;
        Address = client.Client.RemoteEndPoint?.ToString() ?? "?";
        _writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
    }

    /// <summary>Connects to a server. Lines arrive on a background thread; <paramref name="onClosed"/> runs once at the end.</summary>
    public static TcpLineConnection Connect(string host, int port, Action<string> onLine, Action onClosed, int timeoutMs = 5000)
    {
        var client = new TcpClient();
        if (!client.ConnectAsync(host, port).Wait(timeoutMs))
        {
            client.Dispose();
            throw new TimeoutException($"No answer from {host}:{port}");
        }
        var connection = new TcpLineConnection(client);
        connection.StartReading(onLine, onClosed);
        return connection;
    }

    internal static TcpLineConnection Accept(TcpClient client) => new(client);

    internal void StartReading(Action<string> onLine, Action onClosed)
    {
        var thread = new Thread(() =>
        {
            try
            {
                using var reader = new StreamReader(_client.GetStream(), new UTF8Encoding(false));
                while (!_closed && reader.ReadLine() is { } line) onLine(line);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
            {
            }
            finally
            {
                Close();
                onClosed();
            }
        }) { IsBackground = true, Name = $"net-read {Address}" };
        thread.Start();
    }

    public void Send(string line)
    {
        if (_closed) return;
        try
        {
            lock (_writeLock) _writer.WriteLine(line);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or SocketException)
        {
            Close();
        }
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        try { _client.Close(); } catch (SocketException) { }
    }

    public void Dispose() => Close();
}

/// <summary>Accepts TCP players for a <see cref="WorldHost"/>.</summary>
public sealed class TcpHostListener : IDisposable
{
    private readonly TcpListener _listener;
    private readonly WorldHost _host;
    private volatile bool _stopped;

    public int Port { get; }

    public TcpHostListener(WorldHost host, int port, IPAddress? bindTo = null)
    {
        _host = host;
        _listener = new TcpListener(bindTo ?? IPAddress.Any, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        new Thread(AcceptLoop) { IsBackground = true, Name = "net-accept" }.Start();
    }

    private void AcceptLoop()
    {
        while (!_stopped)
        {
            try
            {
                var connection = TcpLineConnection.Accept(_listener.AcceptTcpClient());
                connection.StartReading(line => _host.Receive(connection, line), () => _host.Disconnected(connection));
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                if (_stopped) return;
            }
        }
    }

    public void Dispose()
    {
        _stopped = true;
        _listener.Stop();
    }
}

/// <summary>Runs a <see cref="WorldHost"/> on its own thread at a steady rate, with periodic autosaves.</summary>
public sealed class HostRunner : IDisposable
{
    private readonly WorldHost _host;
    private readonly Action<WorldHost>? _autosave;
    private readonly TimeSpan _autosaveEvery;
    private readonly Thread _thread;
    private volatile bool _stopped;
    private volatile bool _saveRequested;

    public HostRunner(WorldHost host, Action<WorldHost>? autosave = null, TimeSpan? autosaveEvery = null, int updatesPerSecond = 20)
    {
        _host = host;
        _autosave = autosave;
        _autosaveEvery = autosaveEvery ?? TimeSpan.FromMinutes(5);
        _thread = new Thread(() => Loop(updatesPerSecond)) { IsBackground = true, Name = "world-host" };
        _thread.Start();
    }

    private void Loop(int updatesPerSecond)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double last = 0;
        var nextSave = DateTimeOffset.UtcNow + _autosaveEvery;
        while (!_stopped)
        {
            double now = clock.Elapsed.TotalSeconds;
            _host.Update(now - last);
            last = now;
            if (_autosave != null && (_saveRequested || DateTimeOffset.UtcNow >= nextSave))
            {
                _saveRequested = false;
                _autosave(_host);
                nextSave = DateTimeOffset.UtcNow + _autosaveEvery;
            }
            Thread.Sleep(1000 / updatesPerSecond);
        }
    }

    /// <summary>Asks for a save on the host's own thread at its next update.</summary>
    public void SaveSoon() => _saveRequested = true;

    /// <summary>Stops the loop, then saves one last time.</summary>
    public void Dispose()
    {
        _stopped = true;
        _thread.Join();
        _autosave?.Invoke(_host);
    }
}
