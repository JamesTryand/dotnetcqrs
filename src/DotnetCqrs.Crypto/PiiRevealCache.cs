namespace DotnetCqrs.Crypto;

/// <summary>Option A of the read-side design (<c>findings.md</c>, "Principles and
/// decisions"): decrypted values held in this process's memory, so a warm read makes no
/// round trip to the key service and keeps working while it is down (P5).
///
/// <para>Keyed by (subject, ciphertext), so an entry can only ever answer for the exact
/// ciphertext the facade decrypted. Holds the raw plaintext bytes rather than a typed
/// value: the same ciphertext decodes the same way for any <c>Pii&lt;T&gt;</c>, so one
/// cache serves every field type. Bounded by entry count and evicted least recently used.</para>
///
/// <para><b>Per-process and never shared or persisted</b> (P4: what can't forget holds
/// ciphertext). Register it as a singleton for the process, and never back it with a
/// distributed cache.</para>
///
/// <para><b>Erasure.</b> <see cref="MarkErased"/> drops the subject's entries and records
/// the subject in an erased set. Later fills for that subject are ignored, and reveals
/// for it answer "redacted" without a round trip. The set closes the fill-after-evict
/// race: a flush that started before the erasure and finishes after it would otherwise
/// put the plaintext straight back. The set only grows. Erasures are rare and a subject
/// id is small, so it is not bounded. Two things feed it: <see cref="PiiCacheEvictor"/>
/// on <c>SubjectErased</c>, and <see cref="PiiRevealBuffer"/> when the facade reports a
/// destroyed key.</para></summary>
public sealed class PiiRevealCache
{
    /// <summary>The default entry bound. Sized for a modest host, not tuned.</summary>
    public const int DefaultCapacity = 10_000;

    private readonly int _capacity;
    private readonly Lock _lock = new();
    private readonly Dictionary<(string Subject, string Ciphertext), LinkedListNode<Entry>> _entries = new();
    private readonly Dictionary<string, HashSet<string>> _bySubject = new();
    private readonly LinkedList<Entry> _lru = new(); // most recently used first
    private readonly HashSet<string> _erased = [];

    public PiiRevealCache(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    /// <summary>The number of cached entries.</summary>
    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    /// <summary>True once this process has learned the subject is erased.</summary>
    public bool IsErased(string subjectId)
    {
        lock (_lock) return _erased.Contains(subjectId);
    }

    /// <summary>A cached plaintext for exactly this ciphertext, marking it most recently
    /// used. Always misses for an erased subject.</summary>
    public bool TryGet(string subjectId, string ciphertext, out byte[] plaintext)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue((subjectId, ciphertext), out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                plaintext = node.Value.Plaintext;
                return true;
            }
        }
        plaintext = [];
        return false;
    }

    /// <summary>Caches a plaintext the facade just returned. Ignored for an erased
    /// subject, which is how a fill that lost the race with an erasure is dropped.</summary>
    public void Put(string subjectId, string ciphertext, byte[] plaintext)
    {
        lock (_lock)
        {
            if (_erased.Contains(subjectId)) return;
            var key = (subjectId, ciphertext);
            if (_entries.TryGetValue(key, out var existing))
            {
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return;
            }

            var node = _lru.AddFirst(new Entry(subjectId, ciphertext, plaintext));
            _entries[key] = node;
            if (!_bySubject.TryGetValue(subjectId, out var set))
                _bySubject[subjectId] = set = [];
            set.Add(ciphertext);

            while (_entries.Count > _capacity)
                RemoveNode(_lru.Last!);
        }
    }

    /// <summary>Drops every entry for the subject and refuses future fills for it.
    /// Idempotent.</summary>
    public void MarkErased(string subjectId)
    {
        lock (_lock)
        {
            _erased.Add(subjectId);
            if (!_bySubject.Remove(subjectId, out var ciphertexts)) return;
            foreach (var c in ciphertexts)
                if (_entries.Remove((subjectId, c), out var node))
                    _lru.Remove(node);
        }
    }

    private void RemoveNode(LinkedListNode<Entry> node)
    {
        var e = node.Value;
        _lru.Remove(node);
        _entries.Remove((e.Subject, e.Ciphertext));
        if (_bySubject.TryGetValue(e.Subject, out var set))
        {
            set.Remove(e.Ciphertext);
            if (set.Count == 0) _bySubject.Remove(e.Subject);
        }
    }

    private sealed record Entry(string Subject, string Ciphertext, byte[] Plaintext);
}
