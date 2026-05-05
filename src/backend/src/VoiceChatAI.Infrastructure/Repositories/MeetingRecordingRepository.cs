using Microsoft.EntityFrameworkCore;
using VoiceChatAI.Domain.Entities;
using VoiceChatAI.Domain.Interfaces;
using VoiceChatAI.Infrastructure.Data;

namespace VoiceChatAI.Infrastructure.Repositories;

public class MeetingRecordingRepository : IMeetingRecordingRepository
{
    private readonly AppDbContext _context;

    public MeetingRecordingRepository(AppDbContext context)
    {
        _context = context;
    }

    public async Task<MeetingRecording?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.MeetingRecordings.FindAsync(new object[] { id }, cancellationToken);
    }

    public async Task<IEnumerable<MeetingRecording>> GetByRoomIdAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        return await _context.MeetingRecordings
            .Where(r => r.RoomId == roomId)
            .OrderByDescending(r => r.StartedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<MeetingRecording> CreateAsync(MeetingRecording recording, CancellationToken cancellationToken = default)
    {
        _context.MeetingRecordings.Add(recording);
        await _context.SaveChangesAsync(cancellationToken);
        return recording;
    }

    public async Task UpdateAsync(MeetingRecording recording, CancellationToken cancellationToken = default)
    {
        _context.MeetingRecordings.Update(recording);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IEnumerable<MeetingRecording>> SearchAsync(string searchTerm, int limit = 20, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
            return Enumerable.Empty<MeetingRecording>();

        // Full-text search using PostgreSQL tsvector via raw SQL
        var query = @"
            SELECT * FROM ""MeetingRecordings""
            WHERE ""FullText"" IS NOT NULL
            AND to_tsvector('english', ""FullText"") @@ plainto_tsquery('english', {0})
            ORDER BY ts_rank(to_tsvector('english', ""FullText""), plainto_tsquery('english', {0})) DESC
            LIMIT {1}";

        return await _context.MeetingRecordings
            .FromSqlRaw(query, searchTerm, limit)
            .ToListAsync(cancellationToken);
    }
}
