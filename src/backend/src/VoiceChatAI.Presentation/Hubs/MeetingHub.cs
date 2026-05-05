using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using VoiceChatAI.Application.DTOs;
using VoiceChatAI.Application.Services;
using VoiceChatAI.Presentation.Services;

namespace VoiceChatAI.Presentation.Hubs;

/// <summary>
/// SignalR hub for real-time meeting communication.
/// Handles room management, participant events, and AI-generated messages.
/// </summary>
public class MeetingHub : Hub
{
    private readonly IRoomService _roomService;
    private readonly MeetingRecordingService _recordingService;
    private readonly ILogger<MeetingHub> _logger;

    // Static mapping: ConnectionId -> { RoomId, ParticipantId }
    private static readonly Dictionary<string, (Guid RoomId, Guid ParticipantId)> ConnectedUsers = new();

    public MeetingHub(IRoomService roomService, MeetingRecordingService recordingService, ILogger<MeetingHub> logger)
    {
        _roomService = roomService;
        _recordingService = recordingService;
        _logger = logger;
    }

    /// <summary>
    /// Creates a new meeting room.
    /// </summary>
    public async Task<RoomDto> CreateRoom(string roomName, int maxParticipants = 20)
    {
        var dto = new CreateRoomDto(roomName, maxParticipants);
        var room = await _roomService.CreateRoomAsync(dto);
        _logger.LogInformation("Room created: {RoomId} ({Name})", room.Id, roomName);
        return room;
    }

    /// <summary>
    /// Joins an existing meeting room.
    /// </summary>
    public async Task<RoomDto> JoinRoom(Guid roomId, string userName)
    {
        var participant = await _roomService.JoinRoomAsync(roomId, userName);
        ConnectedUsers[Context.ConnectionId] = (roomId, participant.Id);

        await Groups.AddToGroupAsync(Context.ConnectionId, roomId.ToString());
        await Clients.Group(roomId.ToString()).SendAsync("ParticipantJoined", participant);

        // Notify all participants about updated participant list
        var participants = await _roomService.GetParticipantsAsync(roomId);
        await Clients.Group(roomId.ToString()).SendAsync("ParticipantsUpdated", participants);

        var room = await _roomService.GetRoomAsync(roomId);
        _logger.LogInformation("User {User} joined room {RoomId}", userName, roomId);
        return room!;
    }

    /// <summary>
    /// Leaves the current meeting room.
    /// </summary>
    public async Task LeaveRoom(Guid roomId)
    {
        if (ConnectedUsers.TryGetValue(Context.ConnectionId, out var user))
        {
            await _roomService.LeaveRoomAsync(roomId, user.ParticipantId);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId.ToString());
            await Clients.Group(roomId.ToString()).SendAsync("ParticipantLeft", user.ParticipantId);

            // Notify all participants about updated participant list
            var participants = await _roomService.GetParticipantsAsync(roomId);
            await Clients.Group(roomId.ToString()).SendAsync("ParticipantsUpdated", participants);

            ConnectedUsers.Remove(Context.ConnectionId);
            _logger.LogInformation("User left room {RoomId}", roomId);
        }
    }

    /// <summary>
    /// Ends the meeting room and stops recording if active.
    /// </summary>
    public async Task EndRoom(Guid roomId)
    {
        if (_recordingService.IsRecording(roomId))
        {
            await _recordingService.StopRecordingAsync(roomId);
        }
        await _roomService.EndRoomAsync(roomId);
        await Clients.Group(roomId.ToString()).SendAsync("RoomEnded", roomId);
        _logger.LogInformation("Room ended: {RoomId}", roomId);
    }

    /// <summary>
    /// Sends a chat message to the room.
    /// </summary>
    public async Task SendMessage(Guid roomId, string message)
    {
        if (ConnectedUsers.TryGetValue(Context.ConnectionId, out var user))
        {
            await Clients.Group(roomId.ToString()).SendAsync("MessageReceived", new
            {
                ParticipantId = user.ParticipantId,
                Text = message,
                Timestamp = DateTime.UtcNow
            });
        }
    }

    /// <summary>
    /// Updates speaking status for the participant.
    /// </summary>
    public async Task UpdateSpeakingStatus(Guid roomId, bool isSpeaking, float audioLevel = 0)
    {
        if (ConnectedUsers.TryGetValue(Context.ConnectionId, out var user))
        {
            await Clients.Group(roomId.ToString()).SendAsync("SpeakingStatusChanged", new
            {
                ParticipantId = user.ParticipantId,
                IsSpeaking = isSpeaking,
                AudioLevel = audioLevel
            });
        }
    }

    /// <summary>
    /// Starts recording the meeting.
    /// </summary>
    public async Task StartRecording(Guid roomId)
    {
        try
        {
            var recording = await _recordingService.StartRecordingAsync(roomId);
            await Clients.Group(roomId.ToString()).SendAsync("RecordingStarted", new
            {
                RoomId = roomId,
                RecordingId = recording.Id,
                StartedAt = recording.StartedAt
            });
            _logger.LogInformation("Recording started for room {RoomId}", roomId);
        }
        catch (InvalidOperationException ex)
        {
            await Clients.Caller.SendAsync("RecordingError", new
            {
                RoomId = roomId,
                Error = ex.Message
            });
        }
    }

    /// <summary>
    /// Stops recording the meeting.
    /// </summary>
    public async Task StopRecording(Guid roomId)
    {
        try
        {
            var recording = await _recordingService.StopRecordingAsync(roomId);
            await Clients.Group(roomId.ToString()).SendAsync("RecordingStopped", new
            {
                RoomId = roomId,
                RecordingId = recording.Id,
                DurationSeconds = recording.DurationSeconds,
                EndedAt = recording.EndedAt
            });
            _logger.LogInformation("Recording stopped for room {RoomId}, duration: {Duration}s",
                roomId, recording.DurationSeconds);
        }
        catch (InvalidOperationException ex)
        {
            await Clients.Caller.SendAsync("RecordingError", new
            {
                RoomId = roomId,
                Error = ex.Message
            });
        }
    }

    /// <summary>
    /// Gets recording status for a room.
    /// </summary>
    public Task<bool> GetRecordingStatus(Guid roomId)
    {
        return Task.FromResult(_recordingService.IsRecording(roomId));
    }

    /// <summary>
    /// Gets the list of active rooms.
    /// </summary>
    public async Task<IEnumerable<RoomDto>> GetActiveRooms()
    {
        return await _roomService.GetActiveRoomsAsync();
    }

    /// <summary>
    /// Gets participants in a room.
    /// </summary>
    public async Task<IEnumerable<ParticipantDto>> GetParticipants(Guid roomId)
    {
        return await _roomService.GetParticipantsAsync(roomId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (ConnectedUsers.TryGetValue(Context.ConnectionId, out var user))
        {
            await _roomService.LeaveRoomAsync(user.RoomId, user.ParticipantId);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, user.RoomId.ToString());
            await Clients.Group(user.RoomId.ToString()).SendAsync("ParticipantLeft", user.ParticipantId);

            var participants = await _roomService.GetParticipantsAsync(user.RoomId);
            await Clients.Group(user.RoomId.ToString()).SendAsync("ParticipantsUpdated", participants);

            ConnectedUsers.Remove(Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }
}
