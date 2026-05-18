using Microsoft.Extensions.Logging;
using Moq;
using VoiceChatAI.Application.DTOs;
using VoiceChatAI.Application.Services;
using VoiceChatAI.Domain.Entities;
using VoiceChatAI.Domain.Interfaces;

namespace VoiceChatAI.Application.Tests;

public class RoomServiceTests
{
    private readonly Mock<IRoomRepository> _roomRepoMock;
    private readonly Mock<ITranscriptRepository> _transcriptRepoMock;
    private readonly Mock<ILogger<RoomService>> _loggerMock;
    private readonly RoomService _service;

    public RoomServiceTests()
    {
        _roomRepoMock = new Mock<IRoomRepository>();
        _transcriptRepoMock = new Mock<ITranscriptRepository>();
        _loggerMock = new Mock<ILogger<RoomService>>();
        _service = new RoomService(
            _roomRepoMock.Object,
            _transcriptRepoMock.Object,
            _loggerMock.Object
        );
    }

    [Fact]
    public async Task CreateRoomAsync_ShouldCreateAndReturnDto()
    {
        // Arrange
        var dto = new CreateRoomDto("Test Room", 10);
        var room = new Room("Test Room", 10);

        _roomRepoMock
            .Setup(r => r.CreateAsync(It.IsAny<Room>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Room r, CancellationToken _) => r);

        _roomRepoMock
            .Setup(r => r.GetActiveParticipantCountAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        // Act
        var result = await _service.CreateRoomAsync(dto);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Test Room", result.Name);
        Assert.Equal(10, result.MaxParticipants);
        Assert.Equal("Waiting", result.Status);
        Assert.Equal(0, result.ParticipantCount);

        _roomRepoMock.Verify(r => r.CreateAsync(It.IsAny<Room>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetRoomAsync_WhenRoomExists_ShouldReturnDto()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        var room = new Room("Existing Room", 5);

        _roomRepoMock
            .Setup(r => r.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);

        _roomRepoMock
            .Setup(r => r.GetActiveParticipantCountAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(2);

        // Act
        var result = await _service.GetRoomAsync(roomId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Existing Room", result.Name);
        Assert.Equal(2, result.ParticipantCount);
    }

    [Fact]
    public async Task GetRoomAsync_WhenRoomNotFound_ShouldReturnNull()
    {
        // Arrange
        _roomRepoMock
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Room?)null);

        // Act
        var result = await _service.GetRoomAsync(Guid.NewGuid());

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetActiveRoomsAsync_ShouldReturnOnlyActiveRooms()
    {
        // Arrange
        var rooms = new List<Room>
        {
            new("Room 1", 10),
            new("Room 2", 5),
        };

        _roomRepoMock
            .Setup(r => r.GetActiveRoomsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(rooms);

        _roomRepoMock
            .Setup(r => r.GetActiveParticipantCountAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        // Act
        var result = await _service.GetActiveRoomsAsync();

        // Assert
        Assert.Equal(2, result.Count());
    }

    [Fact]
    public async Task JoinRoomAsync_ShouldAddParticipantAndReturnDto()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        var room = new Room("Test Room", 10);

        _roomRepoMock
            .Setup(r => r.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);

        _roomRepoMock
            .Setup(r => r.GetActiveParticipantsAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Participant>());

        _roomRepoMock
            .Setup(r => r.UpdateAsync(It.IsAny<Room>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _roomRepoMock
            .Setup(r => r.AddParticipantAsync(It.IsAny<Participant>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.JoinRoomAsync(roomId, "Alice");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Alice", result.UserName);
        Assert.False(result.IsSpeaking);

        _roomRepoMock.Verify(r => r.AddParticipantAsync(It.IsAny<Participant>(), It.IsAny<CancellationToken>()), Times.Once);
        _roomRepoMock.Verify(r => r.UpdateAsync(It.IsAny<Room>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task JoinRoomAsync_WhenRoomNotFound_ShouldThrow()
    {
        // Arrange
        _roomRepoMock
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Room?)null);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.JoinRoomAsync(Guid.NewGuid(), "Alice"));
    }

    [Fact]
    public async Task JoinRoomAsync_WhenParticipantAlreadyExists_ShouldReuse()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        var room = new Room("Test Room", 10);
        var existingParticipant = new Participant(roomId, "Alice");

        _roomRepoMock
            .Setup(r => r.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);

        _roomRepoMock
            .Setup(r => r.GetActiveParticipantsAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Participant> { existingParticipant });

        // Act
        var result = await _service.JoinRoomAsync(roomId, "Alice");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Alice", result.UserName);
        Assert.Equal(existingParticipant.Id, result.Id);

        // Не должен создавать нового участника
        _roomRepoMock.Verify(r => r.AddParticipantAsync(It.IsAny<Participant>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LeaveRoomAsync_ShouldRemoveParticipant()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var room = new Room("Test Room", 10);
        var participant = new Participant(roomId, "Alice");

        _roomRepoMock
            .Setup(r => r.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);

        _roomRepoMock
            .Setup(r => r.GetParticipantAsync(roomId, participantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(participant);

        _roomRepoMock
            .Setup(r => r.RemoveParticipantAsync(roomId, participantId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _roomRepoMock
            .Setup(r => r.GetActiveParticipantCountAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1); // ещё есть участники

        // Act
        await _service.LeaveRoomAsync(roomId, participantId);

        // Assert
        _roomRepoMock.Verify(r => r.RemoveParticipantAsync(roomId, participantId, It.IsAny<CancellationToken>()), Times.Once);
        _roomRepoMock.Verify(r => r.UpdateAsync(It.IsAny<Room>(), It.IsAny<CancellationToken>()), Times.Never); // комната не завершена
    }

    [Fact]
    public async Task LeaveRoomAsync_WhenLastParticipant_ShouldEndRoom()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var room = new Room("Test Room", 10);
        var participant = new Participant(roomId, "Alice");

        _roomRepoMock
            .Setup(r => r.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);

        _roomRepoMock
            .Setup(r => r.GetParticipantAsync(roomId, participantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(participant);

        _roomRepoMock
            .Setup(r => r.RemoveParticipantAsync(roomId, participantId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _roomRepoMock
            .Setup(r => r.GetActiveParticipantCountAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(0); // последний участник

        _roomRepoMock
            .Setup(r => r.UpdateAsync(It.IsAny<Room>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _service.LeaveRoomAsync(roomId, participantId);

        // Assert
        _roomRepoMock.Verify(r => r.UpdateAsync(It.IsAny<Room>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(RoomStatus.Ended, room.Status);
    }

    [Fact]
    public async Task LeaveRoomAsync_WhenRoomNotFound_ShouldThrow()
    {
        // Arrange
        _roomRepoMock
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Room?)null);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.LeaveRoomAsync(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public async Task EndRoomAsync_ShouldEndRoom()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        var room = new Room("Test Room", 10);

        _roomRepoMock
            .Setup(r => r.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);

        _roomRepoMock
            .Setup(r => r.UpdateAsync(It.IsAny<Room>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _service.EndRoomAsync(roomId);

        // Assert
        Assert.Equal(RoomStatus.Ended, room.Status);
        _roomRepoMock.Verify(r => r.UpdateAsync(room, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EndRoomAsync_WhenRoomNotFound_ShouldThrow()
    {
        // Arrange
        _roomRepoMock
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Room?)null);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.EndRoomAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task DeleteRoomAsync_ShouldDelete()
    {
        // Arrange
        var roomId = Guid.NewGuid();

        _roomRepoMock
            .Setup(r => r.DeleteAsync(roomId, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        await _service.DeleteRoomAsync(roomId);

        // Assert
        _roomRepoMock.Verify(r => r.DeleteAsync(roomId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetParticipantsAsync_ShouldReturnParticipants()
    {
        // Arrange
        var roomId = Guid.NewGuid();
        var room = new Room("Test Room", 10);
        var participants = new List<Participant>
        {
            new(roomId, "Alice"),
            new(roomId, "Bob"),
        };

        _roomRepoMock
            .Setup(r => r.GetByIdAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(room);

        _roomRepoMock
            .Setup(r => r.GetActiveParticipantsAsync(roomId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(participants);

        // Act
        var result = await _service.GetParticipantsAsync(roomId);

        // Assert
        Assert.Equal(2, result.Count());
        Assert.Contains(result, p => p.UserName == "Alice");
        Assert.Contains(result, p => p.UserName == "Bob");
    }

    [Fact]
    public async Task GetParticipantsAsync_WhenRoomNotFound_ShouldThrow()
    {
        // Arrange
        _roomRepoMock
            .Setup(r => r.GetByIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Room?)null);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.GetParticipantsAsync(Guid.NewGuid()));
    }
}
