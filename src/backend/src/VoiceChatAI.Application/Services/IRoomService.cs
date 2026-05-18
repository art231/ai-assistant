using VoiceChatAI.Application.DTOs;

namespace VoiceChatAI.Application.Services;

public interface IRoomService
{
    Task<RoomDto> CreateRoomAsync(CreateRoomDto dto, CancellationToken cancellationToken = default);
    Task<RoomDto?> GetRoomAsync(Guid roomId, CancellationToken cancellationToken = default);
    Task<IEnumerable<RoomDto>> GetActiveRoomsAsync(CancellationToken cancellationToken = default);
    Task<ParticipantDto> JoinRoomAsync(Guid roomId, string userName, CancellationToken cancellationToken = default);
    Task LeaveRoomAsync(Guid roomId, Guid participantId, CancellationToken cancellationToken = default);
    Task EndRoomAsync(Guid roomId, CancellationToken cancellationToken = default);
    Task DeleteRoomAsync(Guid roomId, CancellationToken cancellationToken = default);
    Task<IEnumerable<ParticipantDto>> GetParticipantsAsync(Guid roomId, CancellationToken cancellationToken = default);
}
