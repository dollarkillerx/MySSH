namespace MySSH.Mcp;

/// <summary>Thread-safe registry behind <see cref="McpServerHost.Clients"/>.</summary>
internal sealed class ClientTracker(TimeProvider time)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _localCounter;

    public event Action? Changed;

    public sealed class Entry
    {
        public required string Id { get; init; }
        public required McpClientTransport Transport { get; init; }
        public string? Name { get; set; }
        public string? Version { get; set; }
        public DateTimeOffset FirstSeen { get; init; }
        public DateTimeOffset LastSeen { get; set; }
        public int CallCount { get; set; }
        public int InFlight { get; set; }
        public bool IsConnected { get; set; }
        /// <summary>Local sessions only: closes the connection.</summary>
        public Func<Task>? Disconnect { get; set; }
    }

    public IReadOnlyList<McpClientInfo> Snapshot()
    {
        lock (_lock)
        {
            return _entries.Values
                .OrderByDescending(e => e.LastSeen)
                .Select(e => new McpClientInfo(e.Id, e.Transport, e.Name, e.Version, e.FirstSeen, e.LastSeen, e.CallCount, e.InFlight, e.IsConnected))
                .ToArray();
        }
    }

    /// <summary>A new local connection (one MCP session).</summary>
    public Entry OpenLocal(Func<Task> disconnect)
    {
        Entry e;
        lock (_lock)
        {
            var now = time.GetUtcNow();
            e = new Entry
            {
                Id = "local:" + ++_localCounter,
                Transport = McpClientTransport.Local,
                FirstSeen = now,
                LastSeen = now,
                IsConnected = true,
                Disconnect = disconnect,
            };
            _entries[e.Id] = e;
        }
        Raise();
        return e;
    }

    public void CloseLocal(Entry e)
    {
        lock (_lock)
        {
            if (!_entries.Remove(e.Id)) return;
            e.IsConnected = false;
        }
        Raise();
    }

    /// <summary>Removes an HTTP entry (it reappears on the client's next request).</summary>
    public void Forget(Entry e)
    {
        lock (_lock)
        {
            if (!_entries.Remove(e.Id)) return;
        }
        Raise();
    }

    /// <summary>The entry an HTTP request belongs to (created on first sight).</summary>
    public Entry Http(string? name, string? version)
    {
        lock (_lock)
        {
            var id = name is null ? "http" : "http:" + name;
            if (!_entries.TryGetValue(id, out var e))
            {
                EvictOldestHttpIfFull();
                var now = time.GetUtcNow();
                e = new Entry { Id = id, Transport = McpClientTransport.Http, Name = name, Version = version, FirstSeen = now, LastSeen = now };
                _entries[id] = e;
            }
            return e;
        }
    }

    /// <summary>HTTP names are client-chosen: keep only the most recently active ones.</summary>
    public const int MaxHttpEntries = 32;

    private void EvictOldestHttpIfFull()
    {
        var http = _entries.Values.Where(x => x.Transport == McpClientTransport.Http).ToList();
        if (http.Count < MaxHttpEntries) return;
        foreach (var old in http.Where(x => x.InFlight == 0).OrderBy(x => x.LastSeen).Take(http.Count - MaxHttpEntries + 1))
            _entries.Remove(old.Id);
    }

    public Entry? Find(string id)
    {
        lock (_lock) return _entries.GetValueOrDefault(id);
    }

    /// <summary>Records activity; local sessions learn their name from the first request that carries clientInfo.</summary>
    public void Touch(Entry e, string? name, string? version)
    {
        lock (_lock)
        {
            e.LastSeen = time.GetUtcNow();
            if (name is not null && e.Transport == McpClientTransport.Local)
            {
                e.Name = name;
                e.Version = version;
            }
            else if (version is not null && e.Version is null)
            {
                e.Version = version;
            }
        }
        Raise();
    }

    public CallScope BeginCall(Entry e)
    {
        lock (_lock)
        {
            e.CallCount++;
            e.InFlight++;
            e.LastSeen = time.GetUtcNow();
        }
        Raise();
        return new CallScope(this, e);
    }

    private void EndCall(Entry e)
    {
        lock (_lock)
        {
            e.InFlight--;
            e.LastSeen = time.GetUtcNow();
        }
        Raise();
    }

    public void Clear()
    {
        lock (_lock) _entries.Clear();
        Raise();
    }

    private void Raise()
    {
        var handler = Changed;
        if (handler is null) return;
        try { handler(); }
        catch (Exception) { /* a subscriber bug must not break a tool call */ }
    }

    public readonly struct CallScope(ClientTracker tracker, Entry entry) : IDisposable
    {
        public void Dispose() => tracker.EndCall(entry);
    }
}
