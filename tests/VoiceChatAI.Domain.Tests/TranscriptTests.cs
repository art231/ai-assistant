using VoiceChatAI.Domain.Entities;

namespace VoiceChatAI.Domain.Tests;

public class TranscriptTests
{
    [Fact]
    public void CreateTranscript_ShouldSetProperties()
    {
        var roomId = Guid.NewGuid();
        var transcript = new Transcript(roomId, "Alice", "Hello world");

        Assert.NotEqual(Guid.Empty, transcript.Id);
        Assert.Equal(roomId, transcript.RoomId);
        Assert.Equal("Alice", transcript.UserName);
        Assert.Equal("Hello world", transcript.Text);
        Assert.True(transcript.IsFinal);
        Assert.Equal("en", transcript.Language);
        Assert.Null(transcript.Embedding);
    }

    [Fact]
    public void CreateTranscript_NullUserName_ShouldThrow()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new Transcript(Guid.NewGuid(), null!, "text"));
    }

    [Fact]
    public void CreateTranscript_NullText_ShouldThrow()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new Transcript(Guid.NewGuid(), "Alice", null!));
    }

    [Fact]
    public void CreateTranscript_WithOptionalParameters()
    {
        var roomId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var transcript = new Transcript(roomId, "Bob", "Test", participantId, false, "ru");

        Assert.Equal(participantId, transcript.ParticipantId);
        Assert.False(transcript.IsFinal);
        Assert.Equal("ru", transcript.Language);
    }

    [Fact]
    public void SetEmbedding_ShouldStoreVector()
    {
        var transcript = new Transcript(Guid.NewGuid(), "Alice", "Test");
        var embedding = new float[] { 0.1f, 0.2f, 0.3f };

        transcript.SetEmbedding(embedding);

        Assert.Equal(embedding, transcript.Embedding);
    }

    [Fact]
    public void SetEmbedding_Null_ShouldThrow()
    {
        var transcript = new Transcript(Guid.NewGuid(), "Alice", "Test");

        Assert.Throws<ArgumentNullException>(() => transcript.SetEmbedding(null!));
    }

    [Fact]
    public void SetFinal_ShouldMarkAsFinal()
    {
        var transcript = new Transcript(Guid.NewGuid(), "Alice", "Test", isFinal: false);

        Assert.False(transcript.IsFinal);

        transcript.SetFinal();

        Assert.True(transcript.IsFinal);
    }
}
