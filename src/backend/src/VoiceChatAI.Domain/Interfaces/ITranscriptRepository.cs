using VoiceChatAI.Domain.Entities;

namespace VoiceChatAI.Domain.Interfaces;

public interface ITranscriptRepository
{
    Task<Transcript?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IEnumerable<Transcript>> GetByRoomIdAsync(Guid roomId, int limit = 50, CancellationToken cancellationToken = default);
    Task<IEnumerable<Transcript>> GetByRoomIdSinceAsync(Guid roomId, DateTime since, CancellationToken cancellationToken = default);
    Task<List<Transcript>> GetRecentByRoomIdAsync(Guid roomId, int count = 50, CancellationToken cancellationToken = default);
    Task<Transcript> CreateAsync(Transcript transcript, CancellationToken cancellationToken = default);
    Task<IEnumerable<Transcript>> CreateRangeAsync(IEnumerable<Transcript> transcripts, CancellationToken cancellationToken = default);
}
