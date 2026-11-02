using System.Security.Cryptography;

namespace Concierge.Shared.Identity;

/// <summary>
/// A short-lived, single-use challenge for a browser or Desktop waiting for approval
/// from an independently authenticated phone. The QR payload contains RequestId;
/// BrowserVerifier stays only in the requesting client. This is challenge state,
/// not an account session or a substitute for authenticating the phone.
/// </summary>
public sealed record QrPairingRequest(string RequestId, string BrowserVerifier, DateTimeOffset ExpiresAt);

public sealed record QrPairingApproval(string OwnerId, string PhoneDeviceId);

public enum QrPairingStatus { Unknown, Pending, Approved, Rejected, Expired }

public sealed class QrPairingChallenges(TimeProvider? clock = null)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private const int MaximumPending = 256;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public QrPairingRequest Create()
    {
        var now = _clock.GetUtcNow();
        var id = Token(16);
        var verifier = Token(32);
        var request = new QrPairingRequest(id, verifier, now.Add(Lifetime));
        lock (_gate)
        {
            RemoveExpired(now);
            if (_entries.Count >= MaximumPending)
                throw new InvalidOperationException("Too many pairing requests are waiting. Try again shortly.");
            _entries.Add(id, new Entry(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier)), request.ExpiresAt));
        }
        return request;
    }

    public QrPairingStatus Read(string requestId, string browserVerifier)
    {
        lock (_gate)
        {
            if (!Find(requestId, browserVerifier, out var entry)) return QrPairingStatus.Unknown;
            if (_clock.GetUtcNow() >= entry.ExpiresAt) return QrPairingStatus.Expired;
            return entry.Status;
        }
    }

    /// <summary>Call only after the phone's account and device have been authenticated.</summary>
    public bool Approve(string requestId, QrPairingApproval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        if (string.IsNullOrEmpty(requestId)) return false;
        if (string.IsNullOrWhiteSpace(approval.OwnerId) || string.IsNullOrWhiteSpace(approval.PhoneDeviceId))
            throw new ArgumentException("The approving phone must have an owner and device identity.", nameof(approval));
        lock (_gate)
        {
            if (!_entries.TryGetValue(requestId, out var entry)
                || _clock.GetUtcNow() >= entry.ExpiresAt || entry.Status != QrPairingStatus.Pending)
                return false;
            entry.Approval = approval;
            entry.Status = QrPairingStatus.Approved;
            return true;
        }
    }

    /// <summary>Call only after the phone's account and device have been authenticated.</summary>
    public bool Reject(string requestId)
    {
        if (string.IsNullOrEmpty(requestId)) return false;
        lock (_gate)
        {
            if (!_entries.TryGetValue(requestId, out var entry)
                || _clock.GetUtcNow() >= entry.ExpiresAt || entry.Status != QrPairingStatus.Pending)
                return false;
            entry.Status = QrPairingStatus.Rejected;
            return true;
        }
    }

    /// <summary>Returns the approved owner once. The caller must issue a real device-bound session.</summary>
    public QrPairingApproval? Consume(string requestId, string browserVerifier)
    {
        lock (_gate)
        {
            if (!Find(requestId, browserVerifier, out var entry)
                || _clock.GetUtcNow() >= entry.ExpiresAt
                || entry.Status != QrPairingStatus.Approved)
                return null;
            _entries.Remove(requestId);
            return entry.Approval;
        }
    }

    private bool Find(string requestId, string verifier, out Entry entry)
    {
        entry = null!;
        if (string.IsNullOrEmpty(requestId) || string.IsNullOrEmpty(verifier)
            || !_entries.TryGetValue(requestId, out var candidate)) return false;
        var digest = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier));
        if (!CryptographicOperations.FixedTimeEquals(digest, candidate.VerifierHash)) return false;
        entry = candidate;
        return true;
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var id in _entries.Where(item => now >= item.Value.ExpiresAt).Select(item => item.Key).ToArray())
            _entries.Remove(id);
    }

    private static string Token(int bytes)
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class Entry(byte[] verifierHash, DateTimeOffset expiresAt)
    {
        public byte[] VerifierHash { get; } = verifierHash;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public QrPairingStatus Status { get; set; } = QrPairingStatus.Pending;
        public QrPairingApproval? Approval { get; set; }
    }
}
