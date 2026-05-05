using VoiceChatAI.Domain.Entities;

namespace VoiceChatAI.Domain.Tests;

public class MeetingRecordingTests
{
    [Fact]
    public void CreateMeetingRecording_ShouldSetProperties()
    {
        var roomId = Guid.NewGuid();
        var recording = new MeetingRecording(roomId, "/recordings/test.ogg");

        Assert.NotEqual(Guid.Empty, recording.Id);
        Assert.Equal(roomId, recording.RoomId);
        Assert.Equal("/recordings/test.ogg", recording.AudioPath);
        Assert.Equal(RecordingStatus.Recording, recording.Status);
        Assert.Null(recording.FullText);
        Assert.Null(recording.Summary);
        Assert.Null(recording.EndedAt);
    }

    [Fact]
    public void CreateMeetingRecording_NullAudioPath_ShouldThrow()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new MeetingRecording(Guid.NewGuid(), null!));
    }

    [Fact]
    public void StopRecording_ShouldSetDurationAndStatus()
    {
        var recording = new MeetingRecording(Guid.NewGuid(), "/recordings/test.ogg");

        Thread.Sleep(1100); // ensure some duration
        recording.StopRecording(1024);

        Assert.Equal(RecordingStatus.Completed, recording.Status);
        Assert.NotNull(recording.EndedAt);
        Assert.True(recording.DurationSeconds >= 1);
        Assert.Equal(1024, recording.FileSizeBytes);
    }

    [Fact]
    public void SetFullText_ShouldStoreText()
    {
        var recording = new MeetingRecording(Guid.NewGuid(), "/recordings/test.ogg");
        recording.SetFullText("This is the full transcript of the meeting.");

        Assert.Equal("This is the full transcript of the meeting.", recording.FullText);
    }

    [Fact]
    public void SetFullText_Null_ShouldThrow()
    {
        var recording = new MeetingRecording(Guid.NewGuid(), "/recordings/test.ogg");

        Assert.Throws<ArgumentNullException>(() => recording.SetFullText(null!));
    }

    [Fact]
    public void SetSummary_ShouldStoreSummary()
    {
        var recording = new MeetingRecording(Guid.NewGuid(), "/recordings/test.ogg");
        recording.SetSummary("Meeting summary");

        Assert.Equal("Meeting summary", recording.Summary);
    }

    [Fact]
    public void MarkFailed_ShouldSetStatus()
    {
        var recording = new MeetingRecording(Guid.NewGuid(), "/recordings/test.ogg");

        recording.MarkFailed();

        Assert.Equal(RecordingStatus.Failed, recording.Status);
        Assert.NotNull(recording.EndedAt);
    }
}
