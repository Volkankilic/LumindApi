using System.Collections.Concurrent;

namespace Chatbot.Application.Chat;

/// <summary>
/// One send at a time per conversation, in this process. It keeps two requests with the same client message id
/// from producing two replies: the second waits, then finds the first one's reply and replays it.
/// The unique index (conversation_id, client_message_id) still protects the data when there are several instances.
/// </summary>
public sealed class ConversationLocks
{
    private readonly ConcurrentDictionary<Guid, Entry> _locks = new();

    public async Task<IDisposable> AcquireAsync(Guid conversationId, CancellationToken ct)
    {
        Entry entry;
        while (true)
        {
            entry = _locks.GetOrAdd(conversationId, _ => new Entry());
            lock (entry)
            {
                if (entry.Retired)
                {
                    continue; // removed between GetOrAdd and here: take a fresh one
                }

                entry.Users++;
            }

            break;
        }

        try
        {
            await entry.Semaphore.WaitAsync(ct);
        }
        catch
        {
            Release(conversationId, entry, wasHeld: false);
            throw;
        }

        return new Releaser(() => Release(conversationId, entry, wasHeld: true));
    }

    private void Release(Guid conversationId, Entry entry, bool wasHeld)
    {
        if (wasHeld)
        {
            entry.Semaphore.Release();
        }

        lock (entry)
        {
            entry.Users--;
            if (entry.Users == 0)
            {
                entry.Retired = true;
                _locks.TryRemove(new KeyValuePair<Guid, Entry>(conversationId, entry));
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int Users { get; set; }

        public bool Retired { get; set; }
    }

    private sealed class Releaser(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
