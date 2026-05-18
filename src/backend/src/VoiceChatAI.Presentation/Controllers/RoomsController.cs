using Microsoft.AspNetCore.Mvc;
using VoiceChatAI.Application.DTOs;
using VoiceChatAI.Application.Services;

namespace VoiceChatAI.Presentation.Controllers;

[ApiController]
[Route("api/rooms")]
public class RoomsController : ControllerBase
{
    private readonly IRoomService _roomService;
    private readonly ILogger<RoomsController> _logger;

    public RoomsController(IRoomService roomService, ILogger<RoomsController> logger)
    {
        _roomService = roomService;
        _logger = logger;
    }

    /// <summary>
    /// POST /api/rooms — создать новую комнату
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<RoomDto>> CreateRoom([FromBody] CreateRoomDto dto, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dto.Name))
            return BadRequest(new { error = "Room name is required" });

        var room = await _roomService.CreateRoomAsync(dto, cancellationToken);
        _logger.LogInformation("Room created: {RoomId} ({RoomName})", room.Id, room.Name);
        return CreatedAtAction(nameof(GetRoom), new { id = room.Id }, room);
    }

    /// <summary>
    /// GET /api/rooms — список активных комнат
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<RoomDto>>> GetActiveRooms(CancellationToken cancellationToken)
    {
        var rooms = await _roomService.GetActiveRoomsAsync(cancellationToken);
        return Ok(rooms);
    }

    /// <summary>
    /// GET /api/rooms/{id} — получить комнату по ID
    /// </summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<RoomDto>> GetRoom(Guid id, CancellationToken cancellationToken)
    {
        var room = await _roomService.GetRoomAsync(id, cancellationToken);
        if (room is null)
            return NotFound(new { error = $"Room {id} not found" });
        return Ok(room);
    }

    /// <summary>
    /// POST /api/rooms/{id}/join — присоединиться к комнате
    /// </summary>
    [HttpPost("{id:guid}/join")]
    public async Task<ActionResult<ParticipantDto>> JoinRoom(Guid id, [FromBody] JoinRoomRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.UserName))
            return BadRequest(new { error = "UserName is required" });

        try
        {
            var participant = await _roomService.JoinRoomAsync(id, request.UserName, cancellationToken);
            _logger.LogInformation("Participant {UserName} joined room {RoomId}", request.UserName, id);
            return Ok(participant);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>
    /// POST /api/rooms/{id}/leave — покинуть комнату
    /// </summary>
    [HttpPost("{id:guid}/leave")]
    public async Task<IActionResult> LeaveRoom(Guid id, [FromBody] LeaveRoomRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await _roomService.LeaveRoomAsync(id, request.ParticipantId, cancellationToken);
            _logger.LogInformation("Participant {ParticipantId} left room {RoomId}", request.ParticipantId, id);
            return Ok(new { message = "Left room successfully" });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// POST /api/rooms/{id}/end — завершить комнату
    /// </summary>
    [HttpPost("{id:guid}/end")]
    public async Task<IActionResult> EndRoom(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await _roomService.EndRoomAsync(id, cancellationToken);
            _logger.LogInformation("Room {RoomId} ended", id);
            return Ok(new { message = "Room ended successfully" });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }

    /// <summary>
    /// DELETE /api/rooms/{id} — удалить комнату
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteRoom(Guid id, CancellationToken cancellationToken)
    {
        await _roomService.DeleteRoomAsync(id, cancellationToken);
        _logger.LogInformation("Room {RoomId} deleted", id);
        return NoContent();
    }

    /// <summary>
    /// GET /api/rooms/{id}/participants — список участников комнаты
    /// </summary>
    [HttpGet("{id:guid}/participants")]
    public async Task<ActionResult<IEnumerable<ParticipantDto>>> GetParticipants(Guid id, CancellationToken cancellationToken)
    {
        try
        {
            var participants = await _roomService.GetParticipantsAsync(id, cancellationToken);
            return Ok(participants);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }
    }
}

// Request DTOs
public class JoinRoomRequest
{
    public string UserName { get; set; } = string.Empty;
}

public class LeaveRoomRequest
{
    public Guid ParticipantId { get; set; }
}
