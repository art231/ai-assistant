using VoiceChatAI.Domain.Entities;

namespace VoiceChatAI.Domain.Interfaces;

public interface IRoomRepository
{
    Task<Room?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IEnumerable<Room>> GetActiveRoomsAsync(CancellationToken cancellationToken = default);
    Task<Room> CreateAsync(Room room, CancellationToken cancellationToken = default);
    Task UpdateAsync(Room room, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
