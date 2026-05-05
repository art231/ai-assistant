using VoiceChatAI.Domain.Entities;

namespace VoiceChatAI.Domain.Tests;

public class ParticipantTests
{
    [Fact]
    public void CreateParticipant_ShouldSetProperties()
    {
        var roomId = Guid.NewGuid();
        var participant = new Participant(roomId, "Alice");

        Assert.NotEqual(Guid.Empty, participant.Id);
        Assert.Equal(roomId, participant.RoomId);
        Assert.Equal("Alice", participant.UserName);
        Assert.False(participant.IsSpeaking);
        Assert.Equal(0.0f, participant.AudioLevel);
        Assert.True(participant.IsActive);
        Assert.Null(participant.LeftAt);
    }

    [Fact]
    public void CreateParticipant_NullUserName_ShouldThrow()
    {
        Assert.Throws<ArgumentNullException>(() => new Participant(Guid.NewGuid(), null!));
    }

    [Fact]
    public void MarkLeft_ShouldSetLeftAtAndDeactivate()
    {
        var participant = new Participant(Guid.NewGuid(), "Bob");
        participant.MarkLeft();

        Assert.NotNull(participant.LeftAt);
        Assert.False(participant.IsSpeaking);
        Assert.Equal(0.0f, participant.AudioLevel);
        Assert.False(participant.IsActive);
    }

    [Fact]
    public void SetSpeaking_ShouldUpdateState()
    {
        var participant = new Participant(Guid.NewGuid(), "Charlie");

        participant.SetSpeaking(true, 0.75f);

        Assert.True(participant.IsSpeaking);
        Assert.Equal(0.75f, participant.AudioLevel);

        participant.SetSpeaking(false);

        Assert.False(participant.IsSpeaking);
        Assert.Equal(0.0f, participant.AudioLevel);
    }
}
