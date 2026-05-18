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
    public async Task<RoomDto> JoinRoom(string roomId, string userName)
    {
        var guid = Guid.Parse(roomId);
        var participant = await _roomService.JoinRoomAsync(guid, userName);
        ConnectedUsers[Context.ConnectionId] = (guid, participant.Id);

        await Groups.AddToGroupAsync(Context.ConnectionId, guid.ToString());
        await Clients.Group(guid.ToString()).SendAsync("ParticipantJoined", participant);

        // Notify all participants about updated participant list
        var participants = await _roomService.GetParticipantsAsync(guid);
        await Clients.Group(guid.ToString()).SendAsync("ParticipantsUpdated", participants);

        var room = await _roomService.GetRoomAsync(guid);
        _logger.LogInformation("User {User} joined room {RoomId}", userName, guid);
        return room!;
    }

    /// <summary>
    /// Leaves the current meeting room.
    /// </summary>
    public async Task LeaveRoom(string roomId)
    {
        var guid = Guid.Parse(roomId);
        if (ConnectedUsers.TryGetValue(Context.ConnectionId, out var user))
        {
            await _roomService.LeaveRoomAsync(guid, user.ParticipantId);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, guid.ToString());
            await Clients.Group(guid.ToString()).SendAsync("ParticipantLeft", user.ParticipantId);

            // Notify all participants about updated participant list
            var participants = await _roomService.GetParticipantsAsync(guid);
            await Clients.Group(guid.ToString()).SendAsync("ParticipantsUpdated", participants);

            ConnectedUsers.Remove(Context.ConnectionId);
            _logger.LogInformation("User left room {RoomId}", guid);
        }
    }

    /// <summary>
    /// Ends the meeting room and stops recording if active.
    /// </summary>
    public async Task EndRoom(string roomId)
    {
        var guid = Guid.Parse(roomId);
        if (_recordingService.IsRecording(guid))
        {
            await _recordingService.StopRecordingAsync(guid);
        }
        await _roomService.EndRoomAsync(guid);
        await Clients.Group(guid.ToString()).SendAsync("RoomEnded", guid);
        _logger.LogInformation("Room ended: {RoomId}", guid);
    }

    /// <summary>
    /// Sends a chat message to the room.
    /// </summary>
    public async Task SendMessage(string roomId, string message)
    {
        var guid = Guid.Parse(roomId);
        if (ConnectedUsers.TryGetValue(Context.ConnectionId, out var user))
        {
            await Clients.Group(guid.ToString()).SendAsync("MessageReceived", new
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
    public async Task UpdateSpeakingStatus(string roomId, bool isSpeaking, float audioLevel = 0)
    {
        var guid = Guid.Parse(roomId);
        if (ConnectedUsers.TryGetValue(Context.ConnectionId, out var user))
        {
            await Clients.Group(guid.ToString()).SendAsync("SpeakingStatusChanged", new
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
    public async Task StartRecording(string roomId)
    {
        var guid = Guid.Parse(roomId);
        try
        {
            var recording = await _recordingService.StartRecordingAsync(guid);
            await Clients.Group(guid.ToString()).SendAsync("RecordingStarted", new
            {
                RoomId = guid,
                RecordingId = recording.Id,
                StartedAt = recording.StartedAt
            });
            _logger.LogInformation("Recording started for room {RoomId}", guid);
        }
        catch (InvalidOperationException ex)
        {
            await Clients.Caller.SendAsync("RecordingError", new
            {
                RoomId = guid,
                Error = ex.Message
            });
        }
    }

    /// <summary>
    /// Stops recording the meeting.
    /// </summary>
    public async Task StopRecording(string roomId)
    {
        var guid = Guid.Parse(roomId);
        try
        {
            var recording = await _recordingService.StopRecordingAsync(guid);
            await Clients.Group(guid.ToString()).SendAsync("RecordingStopped", new
            {
                RoomId = guid,
                RecordingId = recording.Id,
                DurationSeconds = recording.DurationSeconds,
                EndedAt = recording.EndedAt
            });
            _logger.LogInformation("Recording stopped for room {RoomId}, duration: {Duration}s",
                guid, recording.DurationSeconds);
        }
        catch (InvalidOperationException ex)
        {
            await Clients.Caller.SendAsync("RecordingError", new
            {
                RoomId = guid,
                Error = ex.Message
            });
        }
    }

    /// <summary>
    /// Gets recording status for a room.
    /// </summary>
    public Task<bool> GetRecordingStatus(string roomId)
    {
        var guid = Guid.Parse(roomId);
        return Task.FromResult(_recordingService.IsRecording(guid));
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
    public async Task<IEnumerable<ParticipantDto>> GetParticipants(string roomId)
    {
        var guid = Guid.Parse(roomId);
        return await _roomService.GetParticipantsAsync(guid);
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("SignalR client connected: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("SignalR client disconnected: {ConnectionId}, Error: {Error}",
            Context.ConnectionId, exception?.Message ?? "none");

        if (ConnectedUsers.TryGetValue(Context.ConnectionId, out var user))
        {
            _logger.LogInformation("Removing user from room {RoomId} on disconnect", user.RoomId);
            await _roomService.LeaveRoomAsync(user.RoomId, user.ParticipantId);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, user.RoomId.ToString());
            await Clients.Group(user.RoomId.ToString()).SendAsync("ParticipantLeft", user.ParticipantId);

            var participants = await _roomService.GetParticipantsAsync(user.RoomId);
            await Clients.Group(user.RoomId.ToString()).SendAsync("ParticipantsUpdated", participants);

            ConnectedUsers.Remove(Context.ConnectionId);
        }

        if (exception != null)
        {
            _logger.LogError(exception, "SignalR client disconnected with error: {ConnectionId}", Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }
}
