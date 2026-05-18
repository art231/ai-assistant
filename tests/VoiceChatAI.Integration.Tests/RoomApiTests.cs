using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VoiceChatAI.Application.DTOs;
using VoiceChatAI.Infrastructure.Data;

namespace VoiceChatAI.Integration.Tests;

/// <summary>
/// Integration tests for Room API endpoints.
/// Uses WebApplicationFactory with in-memory database.
/// </summary>
public class RoomApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RoomApiTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Remove existing DbContext registration
                var descriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (descriptor != null)
                    services.Remove(descriptor);

                // Add in-memory database
                services.AddDbContext<AppDbContext>(options =>
                    options.UseInMemoryDatabase("TestDb_RoomApi"));
            });
        });
    }

    [Fact]
    public async Task CreateRoom_ShouldReturnCreatedRoom()
    {
        // Arrange
        var client = _factory.CreateClient();
        var dto = new CreateRoomDto("Integration Test Room", 5);

        // Act
        var response = await client.PostAsJsonAsync("/api/rooms", dto);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var room = await response.Content.ReadFromJsonAsync<RoomDto>();
        Assert.NotNull(room);
        Assert.Equal("Integration Test Room", room.Name);
        Assert.Equal(5, room.MaxParticipants);
        Assert.Equal("Waiting", room.Status);
        Assert.NotEqual(Guid.Empty, room.Id);
    }

    [Fact]
    public async Task CreateRoom_WithDefaultMaxParticipants_ShouldUse20()
    {
        // Arrange
        var client = _factory.CreateClient();
        var dto = new CreateRoomDto("Default Room");

        // Act
        var response = await client.PostAsJsonAsync("/api/rooms", dto);

        // Assert
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var room = await response.Content.ReadFromJsonAsync<RoomDto>();
        Assert.NotNull(room);
        Assert.Equal(20, room.MaxParticipants);
    }

    [Fact]
    public async Task GetRoom_ShouldReturnRoom()
    {
        // Arrange
        var client = _factory.CreateClient();
        var createDto = new CreateRoomDto("Get Test Room");
        var createResponse = await client.PostAsJsonAsync("/api/rooms", createDto);
        var created = await createResponse.Content.ReadFromJsonAsync<RoomDto>();
        Assert.NotNull(created);

        // Act
        var response = await client.GetAsync($"/api/rooms/{created.Id}");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var room = await response.Content.ReadFromJsonAsync<RoomDto>();
        Assert.NotNull(room);
        Assert.Equal(created.Id, room.Id);
        Assert.Equal("Get Test Room", room.Name);
    }

    [Fact]
    public async Task GetRoom_NotFound_ShouldReturn404()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.GetAsync($"/api/rooms/{Guid.NewGuid()}");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetActiveRooms_ShouldReturnOnlyActive()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Create two rooms
        await client.PostAsJsonAsync("/api/rooms", new CreateRoomDto("Room 1"));
        await client.PostAsJsonAsync("/api/rooms", new CreateRoomDto("Room 2"));

        // Act
        var response = await client.GetAsync("/api/rooms");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rooms = await response.Content.ReadFromJsonAsync<List<RoomDto>>();
        Assert.NotNull(rooms);
        Assert.Equal(2, rooms.Count);
    }

    [Fact]
    public async Task JoinRoom_ShouldAddParticipant()
    {
        // Arrange
        var client = _factory.CreateClient();
        var createResponse = await client.PostAsJsonAsync("/api/rooms", new CreateRoomDto("Join Test"));
        var room = await createResponse.Content.ReadFromJsonAsync<RoomDto>();
        Assert.NotNull(room);

        // Act
        var response = await client.PostAsync($"/api/rooms/{room.Id}/join?userName=Alice", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var participant = await response.Content.ReadFromJsonAsync<ParticipantDto>();
        Assert.NotNull(participant);
        Assert.Equal("Alice", participant.UserName);
        Assert.False(participant.IsSpeaking);
    }

    [Fact]
    public async Task JoinRoom_WhenRoomNotFound_ShouldReturn404()
    {
        // Arrange
        var client = _factory.CreateClient();

        // Act
        var response = await client.PostAsync($"/api/rooms/{Guid.NewGuid()}/join?userName=Alice", null);

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task JoinRoom_DuplicateName_ShouldReuseParticipant()
    {
        // Arrange
        var client = _factory.CreateClient();
        var createResponse = await client.PostAsJsonAsync("/api/rooms", new CreateRoomDto("Duplicate Test"));
        var room = await createResponse.Content.ReadFromJsonAsync<RoomDto>();
        Assert.NotNull(room);

        // Join first time
        var firstJoin = await client.PostAsync($"/api/rooms/{room.Id}/join?userName=Alice", null);
        var firstParticipant = await firstJoin.Content.ReadFromJsonAsync<ParticipantDto>();
        Assert.NotNull(firstParticipant);

        // Act - join again with same name
        var secondJoin = await client.PostAsync($"/api/rooms/{room.Id}/join?userName=Alice", null);
        var secondParticipant = await secondJoin.Content.ReadFromJsonAsync<ParticipantDto>();
        Assert.NotNull(secondParticipant);

        // Assert - should be the same participant (reused)
        Assert.Equal(firstParticipant.Id, secondParticipant.Id);
    }

    [Fact]
    public async Task LeaveRoom_ShouldRemoveParticipant()
    {
        // Arrange
        var client = _factory.CreateClient();
        var createResponse = await client.PostAsJsonAsync("/api/rooms", new CreateRoomDto("Leave Test"));
        var room = await createResponse.Content.ReadFromJsonAsync<RoomDto>();
        Assert.NotNull(room);

        var joinResponse = await client.PostAsync($"/api/rooms/{room.Id}/join?userName=Alice", null);
        var participant = await joinResponse.Content.ReadFromJsonAsync<ParticipantDto>();
        Assert.NotNull(participant);

        // Act
        var response = await client.DeleteAsync($"/api/rooms/{room.Id}/participants/{participant.Id}");

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task LeaveRoom_WhenLastParticipant_ShouldEndRoom()
    {
        // Arrange
        var client = _factory.CreateClient();
        var createResponse = await client.PostAsJsonAsync("/api/rooms", new CreateRoomDto("Last Leave"));
        var room = await createResponse.Content.ReadFromJsonAsync<RoomDto>();
        Assert.NotNull(room);

        var joinResponse = await client.PostAsync($"/api/rooms/{room.Id}/join?userName=Alice", null);
        var participant = await joinResponse.Content.ReadFromJsonAsync<ParticipantDto>();
        Assert.NotNull(participant);

        // Act
        await client.DeleteAsync($"/api/rooms/{room.Id}/participants/{participant.Id}");

        // Assert - room should be ended
        var getResponse = await client.GetAsync($"/api/rooms/{room.Id}");
        var endedRoom = await getResponse.Content.ReadFromJsonAsync<RoomDto>();
        Assert.NotNull(endedRoom);
        Assert.Equal("Ended", endedRoom.Status);
    }

    [Fact]
    public async Task GetParticipants_ShouldReturnAllParticipants()
    {
        // Arrange
        var client = _factory.CreateClient();
        var createResponse = await client.PostAsJsonAsync("/api/rooms", new CreateRoomDto("Participants Test"));
        var room = await createResponse.Content.ReadFromJsonAsync<RoomDto>();
        Assert.NotNull(room);

        await client.PostAsync($"/api/rooms/{room.Id}/join?userName=Alice", null);
        await client.PostAsync($"/api/rooms/{room.Id}/join?userName=Bob", null);

        // Act
        var response = await client.GetAsync($"/api/rooms/{room.Id}/participants");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var participants = await response.Content.ReadFromJsonAsync<List<ParticipantDto>>();
        Assert.NotNull(participants);
        Assert.Equal(2, participants.Count);
        Assert.Contains(participants, p => p.UserName == "Alice");
        Assert.Contains(participants, p => p.UserName == "Bob");
    }

    [Fact]
    public async Task EndRoom_ShouldEndRoom()
    {
        // Arrange
        var client = _factory.CreateClient();
        var createResponse = await client.PostAsJsonAsync("/api/rooms", new CreateRoomDto("End Test"));
        var room = await createResponse.Content.ReadFromJsonAsync<RoomDto>();
        Assert.NotNull(room);

        // Act
        var response = await client.PostAsync($"/api/rooms/{room.Id}/end", null);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var getResponse = await client.GetAsync($"/api/rooms/{room.Id}");
        var endedRoom = await getResponse.Content.ReadFromJsonAsync<RoomDto>();
        Assert.NotNull(endedRoom);
        Assert.Equal("Ended", endedRoom.Status);
    }

    [Fact]
    public async Task DeleteRoom_ShouldDeleteRoom()
    {
        // Arrange
        var client = _factory.CreateClient();
        var createResponse = await client.PostAsJsonAsync("/api/rooms", new CreateRoomDto("Delete Test"));
        var room = await createResponse.Content.ReadFromJsonAsync<RoomDto>();
        Assert.NotNull(room);

        // Act
        var response = await client.DeleteAsync($"/api/rooms/{room.Id}");

        // Assert
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var getResponse = await client.GetAsync($"/api/rooms/{room.Id}");
        Assert.Equal(HttpStatusCode.NotFound, getResponse.StatusCode);
    }
}
