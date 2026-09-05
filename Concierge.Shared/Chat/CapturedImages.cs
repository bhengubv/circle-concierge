namespace Concierge.Shared.Chat;

/// <summary>
/// Pictures a capability produced, on their way into the conversation.
///
/// In Chat rather than Devices, and the move is the point: this is the
/// composer's image queue, which a device capability happens to write into. A
/// head with no device layer at all still has one, so the workspace can drain it
/// unconditionally instead of checking whether this build has a camera.
///
/// A tool result is a string. That is fine for every tool Concierge has —
/// until one of them produces an image, and a screenshot described in words is
/// not a screenshot. The conversation store keeps tool results as text events
/// and derives turns from them, so carrying an image down that path means
/// changing how conversations are stored, which is a much larger thing than
/// this and would be the wrong reason to do it.
///
/// The image channel that already exists is the composer's: a person attaches a
/// picture and it rides on their next turn, which is exactly the shape needed.
/// This is the seam that lets a capability put something into the same queue —
/// the capability writes, the workspace drains, and neither has to know about
/// the other.
///
/// Deliberately not a general event bus. It holds pictures until the next turn
/// picks them up, and it caps what it will hold, because a model that calls
/// screenshot in a loop would otherwise fill memory with bitmaps nobody asked
/// for.
/// </summary>
public sealed class CapturedImages
{
    /// <summary>
    /// How many pictures may be waiting at once.
    ///
    /// Four, because a turn carrying more than that is either a mistake or a
    /// context window about to be spent entirely on images. The oldest are
    /// dropped rather than the newest refused: a model that just captured
    /// something wants the thing it just captured.
    /// </summary>
    public const int MaxWaiting = 4;

    private readonly object _gate = new();
    private readonly List<ChatImage> _waiting = new();

    /// <summary>Raised when something arrives, so a surface can say so.</summary>
    public event EventHandler? Changed;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _waiting.Count;
            }
        }
    }

    /// <summary>Queues a picture for the next turn.</summary>
    public void Add(ChatImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        lock (_gate)
        {
            _waiting.Add(image);

            while (_waiting.Count > MaxWaiting)
            {
                _waiting.RemoveAt(0);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Takes everything waiting, leaving the queue empty.
    ///
    /// Draining rather than reading is the whole contract: a picture goes into
    /// exactly one turn. Left in place it would ride on every turn after it, and
    /// a screenshot of a window from ten minutes ago silently attached to an
    /// unrelated question is worse than no screenshot at all.
    /// </summary>
    public IReadOnlyList<ChatImage> TakeAll()
    {
        lock (_gate)
        {
            if (_waiting.Count == 0)
            {
                return [];
            }

            var taken = _waiting.ToArray();
            _waiting.Clear();
            return taken;
        }
    }

    /// <summary>Throws away anything waiting — a new conversation starts clean.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (_waiting.Count == 0)
            {
                return;
            }

            _waiting.Clear();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
