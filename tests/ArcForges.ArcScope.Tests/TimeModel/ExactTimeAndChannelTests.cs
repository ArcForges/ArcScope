// SPDX-License-Identifier: AGPL-3.0-only
using ArcForges.ArcScope.Domain.Channels;
using ArcForges.ArcScope.Domain.Time;
using Xunit;

namespace ArcForges.ArcScope.Tests.TimeModel;

public sealed class ExactTimeAndChannelTests
{
    [Fact]
    public void RatesArePositiveReducedExactRationals()
    {
        Assert.Equal(new ExactRate(44_100, 1), new ExactRate(88_200, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExactRate(0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ExactRate(1, 0));
    }

    [Fact]
    public void DurationConversionIsExactAndNeverRoundsFractionalTicks()
    {
        var audioSamples = new ExactRate(44_100, 1);
        var microseconds = new ExactRate(1_000_000, 1);
        var domain = Domain(1_000_000);

        Assert.Equal(1_000_000, new TimeDuration(44_100, audioSamples).ConvertTo(microseconds).Ticks);
        Assert.Equal(1_000_100, new TimePoint(domain, 100).Add(new TimeDuration(44_100, audioSamples)).Ticks);
        Assert.Throws<InvalidOperationException>(() => new TimeDuration(1, audioSamples).ConvertTo(microseconds));
        Assert.Throws<OverflowException>(() => new TimeDuration(long.MaxValue, new ExactRate(1, 1))
            .ConvertTo(new ExactRate(2, 1)));
    }

    [Fact]
    public void SignalsPreserveIrregularSamplesAndRejectNonMonotonicOrForeignTimes()
    {
        var domain = Domain(10);
        var channel = new Channel<int>(Guid.NewGuid(), "temperature", "°C", new ExactRate(4, 1));
        var signal = new Signal<int>(channel, domain,
        [
            new SignalSample<int>(new TimePoint(domain, 0), 20),
            new SignalSample<int>(new TimePoint(domain, 1), 21),
            new SignalSample<int>(new TimePoint(domain, 4), 23)
        ]);

        Assert.Equal(new[] { 0L, 1L, 4L }, signal.Samples.Select(sample => sample.Time.Ticks));
        Assert.Equal(new[] { 20, 21, 23 }, signal.Samples.Select(sample => sample.Value));
        Assert.Throws<ArgumentException>(() => new Signal<int>(channel, domain,
        [
            new SignalSample<int>(new TimePoint(domain, 1), 1),
            new SignalSample<int>(new TimePoint(domain, 1), 2)
        ]));
        Assert.Throws<ArgumentException>(() => new Signal<int>(channel, domain,
        [new SignalSample<int>(new TimePoint(Domain(10), 1), 1)]));
    }

    [Fact]
    public void DiscreteEventsAreIndependentRecordsNotSignalSamples()
    {
        var domain = Domain(1_000);
        var eventRecord = new EventRecord<string>(
            Guid.NewGuid(), "trigger", new TimePoint(domain, 12_345), "threshold-crossed");

        Assert.Equal("trigger", eventRecord.Kind);
        Assert.Equal(12_345, eventRecord.Time.Ticks);
        Assert.Equal("threshold-crossed", eventRecord.Payload);
    }

    [Fact]
    public void BooleanAndEnumeratedSignalsRetainTypedValuesAndDisplayUnitsAreMetadata()
    {
        var domain = Domain(1_000);
        var digitalChannel = new Channel<bool>(Guid.NewGuid(), "enabled", null, new ExactRate(1, 1));
        var digitalSignal = new Signal<bool>(digitalChannel, domain,
        [new SignalSample<bool>(new TimePoint(domain, 0), false), new SignalSample<bool>(new TimePoint(domain, 1), true)]);
        var modeChannel = new Channel<Mode>(Guid.NewGuid(), "mode", "display only", new ExactRate(2, 1));
        var modeSignal = new Signal<Mode>(modeChannel, domain,
        [new SignalSample<Mode>(new TimePoint(domain, 0), Mode.Idle), new SignalSample<Mode>(new TimePoint(domain, 2), Mode.Active)]);

        Assert.Equal(new[] { false, true }, digitalSignal.Samples.Select(sample => sample.Value));
        Assert.Equal(new[] { Mode.Idle, Mode.Active }, modeSignal.Samples.Select(sample => sample.Value));
        Assert.Equal("display only", modeSignal.Channel.DisplayUnit);
    }

    [Fact]
    public void TwoSourceAlignmentIsRecordedAndMapsOnlyByExactAnchoredOffset()
    {
        var sourceDomain = Domain(1_000);
        var referenceDomain = Domain(44_100);
        var alignment = new SourceAlignment(
            Guid.NewGuid(),
            "source-a",
            "source-b",
            new TimePoint(sourceDomain, 100),
            new TimePoint(referenceDomain, 4_410),
            DateTimeOffset.Parse("2026-09-28T12:00:00Z"));

        var mapped = alignment.MapToReference(new TimePoint(sourceDomain, 2_100));

        Assert.Equal(referenceDomain, mapped.Domain);
        Assert.Equal(92_610, mapped.Ticks);
        Assert.Throws<InvalidOperationException>(() => alignment.MapToReference(new TimePoint(Domain(1_000), 2_100)));
        Assert.Throws<InvalidOperationException>(() => new TimePoint(sourceDomain, 1)
            .CompareTo(new TimePoint(referenceDomain, 44)));
    }

    [Fact]
    public void AlignmentRejectsUnrepresentableCrossRateMappingInsteadOfRounding()
    {
        var sourceDomain = Domain(3);
        var referenceDomain = Domain(10);
        var alignment = new SourceAlignment(
            Guid.NewGuid(), "source-a", "source-b",
            new TimePoint(sourceDomain, 0), new TimePoint(referenceDomain, 0),
            DateTimeOffset.Parse("2026-09-28T12:00:00Z"));

        Assert.Throws<InvalidOperationException>(() => alignment.MapToReference(new TimePoint(sourceDomain, 1)));
    }

    private static TimeDomain Domain(long ticksPerSecond) =>
        new(Guid.NewGuid(), new ExactRate(ticksPerSecond, 1));

    private enum Mode
    {
        Idle,
        Active
    }
}
