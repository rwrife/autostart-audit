using AutostartAudit.Core.Model;
using AutostartAudit.Core.Scan;

namespace AutostartAudit.Core.Tests;

public class ServicePageReaderTests
{
    [Fact]
    public void MoreData_PreservesRecordsAndFollowsContinuation()
    {
        var calls = new List<uint>();
        var records = new List<string>();
        var observations = new List<SourceObservation>();
        ServicePageReader.Read<string>(resume =>
        {
            calls.Add(resume);
            return resume == 0 ? new(new[] { "first" }, 7, 234) : new(new[] { "second" }, 0, 0);
        }, records.Add, observations);
        Assert.Equal(new uint[] { 0, 7 }, calls);
        Assert.Equal(new[] { "first", "second" }, records);
        Assert.Equal(ObservationHealth.Ok, Assert.Single(observations).Health);
    }

    [Fact]
    public void LaterDenial_PreservesEarlierRecordsAndReportsIncompleteScope()
    {
        var records = new List<string>();
        var observations = new List<SourceObservation>();
        ServicePageReader.Read<string>(resume => resume == 0
            ? new(new[] { "readable" }, 9, 234) : new(Array.Empty<string>(), 0, 5), records.Add, observations);
        Assert.Equal("readable", Assert.Single(records));
        Assert.Equal(ObservationHealth.Denied, Assert.Single(observations).Health);
    }

    [Fact]
    public void LaterException_PreservesEarlierRecordsAndReportsUnknown()
    {
        var records = new List<string>();
        var observations = new List<SourceObservation>();
        ServicePageReader.Read<string>(resume => resume == 0
            ? new(new[] { "readable" }, 9, 234) : throw new InvalidOperationException("malformed native page"), records.Add, observations);
        Assert.Equal("readable", Assert.Single(records));
        Assert.Equal(ObservationHealth.Unknown, Assert.Single(observations).Health);
    }

    [Fact]
    public void StuckContinuation_FailsClosedWithoutDroppingReadRecords()
    {
        var calls = 0;
        var records = new List<string>();
        var observations = new List<SourceObservation>();
        ServicePageReader.Read<string>(_ => { calls++; return new(new[] { "readable" }, 0, 234); }, records.Add, observations);
        Assert.Equal(1, calls);
        Assert.Equal("readable", Assert.Single(records));
        Assert.Equal(ObservationHealth.Unknown, Assert.Single(observations).Health);
    }
}
