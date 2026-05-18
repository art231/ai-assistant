using Microsoft.EntityFrameworkCore;
using VoiceChatAI.Domain.Entities;
using VoiceChatAI.Domain.Interfaces;
using VoiceChatAI.Infrastructure.Data;

namespace VoiceChatAI.Infrastructure.Repositories;

public class RoomRepository : IRoomRepository
{
    private readonly AppDbContext _context;

    public RoomRepository(AppDbContext context)
    {
        _context = context;
    }

    public async Task<Room?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.Rooms
            .Include(r => r.ParticipantsNavigation)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    }

    public async Task<IEnumerable<Room>> GetActiveRoomsAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Rooms
            .Where(r => r.Status == RoomStatus.Active || r.Status == RoomStatus.Waiting)
            .ToListAsync(cancellationToken);
    }

    public async Task<Room> CreateAsync(Room room, CancellationToken cancellationToken = default)
    {
        _context.Rooms.Add(room);
        await _context.SaveChangesAsync(cancellationToken);
        return room;
    }

    public async Task UpdateAsync(Room room, CancellationToken cancellationToken = default)
    {
        _context.Rooms.Update(room);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var room = await _context.Rooms.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (room is not null)
        {
            _context.Rooms.Remove(room);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<Participant?> GetParticipantAsync(Guid roomId, Guid participantId, CancellationToken cancellationToken = default)
    {
        return await _context.Participants
            .FirstOrDefaultAsync(p => p.Id == participantId && p.RoomId == roomId, cancellationToken);
    }

    public async Task<IEnumerable<Participant>> GetActiveParticipantsAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        return await _context.Participants
            .Where(p => p.RoomId == roomId && p.LeftAt == null)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> GetActiveParticipantCountAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        return await _context.Participants
            .CountAsync(p => p.RoomId == roomId && p.LeftAt == null, cancellationToken);
    }

    public async Task AddParticipantAsync(Participant participant, CancellationToken cancellationToken = default)
    {
        _context.Participants.Add(participant);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateParticipantAsync(Participant participant, CancellationToken cancellationToken = default)
    {
        _context.Participants.Update(participant);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveParticipantAsync(Guid roomId, Guid participantId, CancellationToken cancellationToken = default)
    {
        var participant = await _context.Participants
            .FirstOrDefaultAsync(p => p.Id == participantId && p.RoomId == roomId, cancellationToken);

        if (participant is not null)
        {
            participant.MarkLeft();
            await _context.SaveChangesAsync(cancellationToken);
        }
    }
}
