using Concierge.Shared.Identity;

namespace Concierge.Tests;

public sealed class QrPairingChallengesTests
{
    [Fact]
    public void OnlyRequestingClientCanConsumeApprovedChallengeOnce()
    {
        var challenges = new QrPairingChallenges();
        var request = challenges.Create();
        var approval = new QrPairingApproval("owner-1", "phone-1");

        Assert.Equal(QrPairingStatus.Unknown, challenges.Read(request.RequestId, "wrong verifier"));
        Assert.Null(challenges.Consume(request.RequestId, "wrong verifier"));
        Assert.Equal(QrPairingStatus.Pending, challenges.Read(request.RequestId, request.BrowserVerifier));
        Assert.True(challenges.Approve(request.RequestId, approval));
        Assert.False(challenges.Approve(request.RequestId, approval));
        Assert.Equal(QrPairingStatus.Approved, challenges.Read(request.RequestId, request.BrowserVerifier));
        Assert.Equal(approval, challenges.Consume(request.RequestId, request.BrowserVerifier));
        Assert.Null(challenges.Consume(request.RequestId, request.BrowserVerifier));
    }

    [Fact]
    public void RejectionNeverYieldsAnAccount()
    {
        var challenges = new QrPairingChallenges();
        var request = challenges.Create();

        Assert.True(challenges.Reject(request.RequestId));
        Assert.Equal(QrPairingStatus.Rejected, challenges.Read(request.RequestId, request.BrowserVerifier));
        Assert.False(challenges.Approve(request.RequestId, new QrPairingApproval("owner-1", "phone-1")));
        Assert.Null(challenges.Consume(request.RequestId, request.BrowserVerifier));
    }

    [Fact]
    public void ExpiredChallengeCannotBeApprovedOrConsumed()
    {
        var clock = new AdjustableTimeProvider();
        var challenges = new QrPairingChallenges(clock);
        var request = challenges.Create();
        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(QrPairingStatus.Expired, challenges.Read(request.RequestId, request.BrowserVerifier));
        Assert.False(challenges.Approve(request.RequestId, new QrPairingApproval("owner-1", "phone-1")));
        Assert.Null(challenges.Consume(request.RequestId, request.BrowserVerifier));
    }

    private sealed class AdjustableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-11-03T00:00:00Z");
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
