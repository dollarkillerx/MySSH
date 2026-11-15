using System.Globalization;
using System.Resources;
using System.Text;
using Avalonia;
using Avalonia.Data;

namespace MySSH.Desktop.Localization;

/// <summary>
/// Runtime-switchable string table backed by Resources/Strings*.resx (en-US neutral, zh-CN, ja-JP).
/// AOT-safe: plain ResourceManager lookups + observable bindings, no reflection bindings.
/// Placeholders use the legacy vue-i18n "{name}" form.
/// </summary>
public sealed class Loc
{
    // Must be declared before Instance: static initializers run in textual order.
    public static readonly IReadOnlyList<string> SupportedCultures = ["en-US", "zh-CN", "ja-JP"];
    public static Loc Instance { get; } = new();

    readonly ResourceManager _rm = new("MySSH.Desktop.Resources.Strings", typeof(Loc).Assembly);
    readonly List<WeakReference<KeyObservable>> _live = [];
    CultureInfo _culture = PickDefault(CultureInfo.CurrentUICulture);

    public CultureInfo Culture => _culture;
    public event Action? CultureChanged;

    public string this[string key] => _rm.GetString(key, _culture) ?? key;

    public string Format(string key, params (string Name, object? Value)[] args)
    {
        var sb = new StringBuilder(this[key]);
        foreach (var (name, value) in args)
            sb.Replace("{" + name + "}", Convert.ToString(value, _culture));
        return sb.ToString();
    }

    /// <summary>null = follow the OS language.</summary>
    public void SetCulture(string? name)
    {
        _culture = name is null ? PickDefault(CultureInfo.CurrentUICulture) : CultureInfo.GetCultureInfo(name);
        lock (_live)
        {
            _live.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (var w in _live) if (w.TryGetTarget(out var o)) o.Publish();
        }
        CultureChanged?.Invoke();
    }

    public BindingBase Bind(string key) => Observe(key).ToBinding();

    /// <summary>Emits the current value on subscribe and again after every culture change.</summary>
    internal IObservable<string> Observe(string key)
    {
        var o = new KeyObservable(this, key);
        lock (_live) _live.Add(new WeakReference<KeyObservable>(o));
        return o;
    }

    static CultureInfo PickDefault(CultureInfo ui)
    {
        foreach (var c in SupportedCultures)
            if (string.Equals(ui.Name, c, StringComparison.OrdinalIgnoreCase)) return CultureInfo.GetCultureInfo(c);
        foreach (var c in SupportedCultures)
            if (string.Equals(ui.TwoLetterISOLanguageName, c[..2], StringComparison.OrdinalIgnoreCase)) return CultureInfo.GetCultureInfo(c);
        return CultureInfo.GetCultureInfo("en-US");
    }

    sealed class KeyObservable(Loc owner, string key) : IObservable<string>
    {
        // Each subscriber is notified on the synchronization context it subscribed on (the UI thread for bindings),
        // so SetCulture may be called from any thread.
        readonly List<(IObserver<string> Observer, SynchronizationContext? Context)> _observers = [];

        public IDisposable Subscribe(IObserver<string> observer)
        {
            var entry = (observer, SynchronizationContext.Current);
            lock (_observers) _observers.Add(entry);
            observer.OnNext(owner[key]);
            return new Unsubscriber(this, entry);
        }

        public void Publish()
        {
            (IObserver<string> Observer, SynchronizationContext? Context)[] snapshot;
            lock (_observers) snapshot = [.. _observers];
            var value = owner[key];
            foreach (var (o, ctx) in snapshot)
            {
                if (ctx is null || ctx == SynchronizationContext.Current) o.OnNext(value);
                else ctx.Post(_ => o.OnNext(value), null);
            }
        }

        sealed class Unsubscriber(KeyObservable src, (IObserver<string>, SynchronizationContext?) entry) : IDisposable
        {
            public void Dispose() { lock (src._observers) src._observers.Remove(entry); }
        }
    }
}

/// <summary>XAML: <c>Text="{l:Tr hosts.title}"</c>. Updates live when <see cref="Loc.SetCulture"/> is called.</summary>
public sealed class TrExtension(string key)
{
    public string Key { get; } = key;
    public BindingBase ProvideValue(IServiceProvider _) => Loc.Instance.Bind(Key);
}
