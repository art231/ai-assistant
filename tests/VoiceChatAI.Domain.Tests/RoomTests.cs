using VoiceChatAI.Domain.Entities;

namespace VoiceChatAI.Domain.Tests;

public class RoomTests
{
    [Fact]
    public void CreateRoom_ShouldSetProperties()
    {
        var room = new Room("Test Room", 10);

        Assert.NotEqual(Guid.Empty, room.Id);
        Assert.Equal("Test Room", room.Name);
        Assert.Equal(RoomStatus.Waiting, room.Status);
        Assert.Equal(10, room.MaxParticipants);
        Assert.Empty(room.Participants);
    }

    [Fact]
    public void CreateRoom_DefaultMaxParticipants_ShouldBe20()
    {
        var room = new Room("Default Room");

        Assert.Equal(20, room.MaxParticipants);
    }

    [Fact]
    public void CreateRoom_NullName_ShouldThrow()
    {
        Assert.Throws<ArgumentNullException>(() => new Room(null!));
    }

    [Fact]
    public void AddParticipant_ShouldAddAndActivateRoom()
    {
        var room = new Room("Test Room");
        var participant = room.AddParticipant("Alice");

        Assert.Single(room.Participants);
        Assert.Equal("Alice", participant.UserName);
        Assert.Equal(RoomStatus.Active, room.Status);
    }

    [Fact]
    public void AddParticipant_DuplicateName_ShouldThrow()
    {
        var room = new Room("Test Room");
        room.AddParticipant("Alice");

        Assert.Throws<InvalidOperationException>(() => room.AddParticipant("Alice"));
    }

    [Fact]
    public void AddParticipant_WhenRoomFull_ShouldThrow()
    {
        var room = new Room("Test Room", 1);
        room.AddParticipant("Alice");

        Assert.Throws<InvalidOperationException>(() => room.AddParticipant("Bob"));
    }

    [Fact]
    public void AddParticipant_WhenRoomEnded_ShouldThrow()
    {
        var room = new Room("Test Room");
        room.End();

        Assert.Throws<InvalidOperationException>(() => room.AddParticipant("Charlie"));
    }

    [Fact]
    public void RemoveParticipant_ShouldMarkLeft()
    {
        var room = new Room("Test Room");
        var participant = room.AddParticipant("Alice");

        room.RemoveParticipant(participant.Id);

        Assert.NotNull(participant.LeftAt);
        Assert.False(participant.IsActive);
    }

    [Fact]
    public void RemoveParticipant_NotFound_ShouldThrow()
    {
        var room = new Room("Test Room");

        Assert.Throws<InvalidOperationException>(() => room.RemoveParticipant(Guid.NewGuid()));
    }

    [Fact]
    public void End_ShouldSetStatusAndMarkAllParticipantsLeft()
    {
        var room = new Room("Test Room");
        room.AddParticipant("Alice");
        room.AddParticipant("Bob");

        room.End();

        Assert.Equal(RoomStatus.Ended, room.Status);
        Assert.NotNull(room.EndedAt);
        Assert.All(room.Participants, p => Assert.NotNull(p.LeftAt));
    }

    [Fact]
    public void AddParticipant_MultipleParticipants_ShouldSucceed()
    {
        var room = new Room("Test Room", 5);

        room.AddParticipant("Alice");
        room.AddParticipant("Bob");
        room.AddParticipant("Charlie");

        Assert.Equal(3, room.Participants.Count);
    }
}
