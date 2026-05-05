using VoiceChatAI.Domain.Entities;

namespace VoiceChatAI.Domain.Interfaces;

public interface IMeetingRecordingRepository
{
    Task<MeetingRecording?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IEnumerable<MeetingRecording>> GetByRoomIdAsync(Guid roomId, CancellationToken cancellationToken = default);
    Task<MeetingRecording> CreateAsync(MeetingRecording recording, CancellationToken cancellationToken = default);
    Task UpdateAsync(MeetingRecording recording, CancellationToken cancellationToken = default);
    Task<IEnumerable<MeetingRecording>> SearchAsync(string searchTerm, int limit = 20, CancellationToken cancellationToken = default);
}
