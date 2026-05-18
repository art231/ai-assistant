using VoiceChatAI.Domain.Entities;

namespace VoiceChatAI.Domain.Interfaces;

public interface IRoomRepository
{
    Task<Room?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IEnumerable<Room>> GetActiveRoomsAsync(CancellationToken cancellationToken = default);
    Task<Room> CreateAsync(Room room, CancellationToken cancellationToken = default);
    Task UpdateAsync(Room room, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Participant?> GetParticipantAsync(Guid roomId, Guid participantId, CancellationToken cancellationToken = default);
    Task<IEnumerable<Participant>> GetActiveParticipantsAsync(Guid roomId, CancellationToken cancellationToken = default);
    Task<int> GetActiveParticipantCountAsync(Guid roomId, CancellationToken cancellationToken = default);
    Task AddParticipantAsync(Participant participant, CancellationToken cancellationToken = default);
    Task UpdateParticipantAsync(Participant participant, CancellationToken cancellationToken = default);
    Task RemoveParticipantAsync(Guid roomId, Guid participantId, CancellationToken cancellationToken = default);
}
