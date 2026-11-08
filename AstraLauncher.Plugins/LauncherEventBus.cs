using AstraLauncher.Plugin.Abstractions;

namespace AstraLauncher.Plugins;

/// <summary>
/// Thread-safe typed event bus. Snapshots are taken before invoking handlers so a handler may subscribe
/// or unsubscribe during dispatch; a failing handler is isolated and reported through the optional callback.
/// </summary>
public sealed class LauncherEventBus : ILauncherEvents
{
    private readonly object _gate = new();
    private readonly Dictionary<Type, List<object>> _handlers = [];
    private readonly Action<Exception>? _onHandlerError;

    public LauncherEventBus(Action<Exception>? onHandlerError = null)
    {
        _onHandlerError = onHandlerError;
    }

    public IDisposable Subscribe<T>(Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            if (!_handlers.TryGetValue(typeof(T), out List<object>? list))
            {
                list = [];
                _handlers.Add(typeof(T), list);
            }

            list.Add(handler);
        }

        return new Subscription(this, typeof(T), handler);
    }

    public void Publish<T>(T message)
    {
        object[] snapshot;
        lock (_gate)
        {
            if (!_handlers.TryGetValue(typeof(T), out List<object>? list) || list.Count == 0)
            {
                return;
            }

            snapshot = list.ToArray();
        }

        foreach (object entry in snapshot)
        {
            try
            {
                ((Action<T>)entry)(message);
            }
            catch (Exception exception)
            {
                _onHandlerError?.Invoke(exception);
            }
        }
    }

    private void Unsubscribe(Type messageType, object handler)
    {
        lock (_gate)
        {
            if (!_handlers.TryGetValue(messageType, out List<object>? list))
            {
                return;
            }

            list.Remove(handler);
            if (list.Count == 0)
            {
                _handlers.Remove(messageType);
            }
        }
    }

    private sealed class Subscription : IDisposable
    {
        private LauncherEventBus? _owner;
        private readonly Type _messageType;
        private readonly object _handler;

        public Subscription(LauncherEventBus owner, Type messageType, object handler)
        {
            _owner = owner;
            _messageType = messageType;
            _handler = handler;
        }

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Unsubscribe(_messageType, _handler);
    }
}
