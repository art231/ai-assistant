using Microsoft.EntityFrameworkCore;
using VoiceChatAI.Domain.Entities;
using VoiceChatAI.Domain.Interfaces;
using VoiceChatAI.Infrastructure.Data;

namespace VoiceChatAI.Infrastructure.Repositories;

public class TranscriptRepository : ITranscriptRepository
{
    private readonly AppDbContext _context;

    public TranscriptRepository(AppDbContext context)
    {
        _context = context;
    }

    public async Task<Transcript?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Transcripts.FindAsync(new object[] { id }, cancellationToken);
    }

    public async Task<IEnumerable<Transcript>> GetByRoomIdAsync(Guid roomId, int limit = 50, CancellationToken cancellationToken = default)
    {
        return await _context.Transcripts
            .Where(t => t.RoomId == roomId && t.IsFinal)
            .OrderByDescending(t => t.Timestamp)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<Transcript>> GetRecentByRoomIdAsync(Guid roomId, int count = 50, CancellationToken cancellationToken = default)
    {
        return await _context.Transcripts
            .Where(t => t.RoomId == roomId && t.IsFinal)
            .OrderByDescending(t => t.Timestamp)
            .Take(count)
            .OrderBy(t => t.Timestamp)
            .ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<Transcript>> GetByRoomIdSinceAsync(Guid roomId, DateTime since, CancellationToken cancellationToken = default)
    {
        return await _context.Transcripts
            .Where(t => t.RoomId == roomId && t.Timestamp >= since && t.IsFinal)
            .OrderBy(t => t.Timestamp)
            .ToListAsync(cancellationToken);
    }

    public async Task<Transcript> CreateAsync(Transcript transcript, CancellationToken cancellationToken = default)
    {
        _context.Transcripts.Add(transcript);
        await _context.SaveChangesAsync(cancellationToken);
        return transcript;
    }

    public async Task<IEnumerable<Transcript>> CreateRangeAsync(IEnumerable<Transcript> transcripts, CancellationToken cancellationToken = default)
    {
        var list = transcripts.ToList();
        _context.Transcripts.AddRange(list);
        await _context.SaveChangesAsync(cancellationToken);
        return list;
    }
}
