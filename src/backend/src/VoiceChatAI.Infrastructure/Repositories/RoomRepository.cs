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
            .Include(r => r.Participants)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
    }

    public async Task<IEnumerable<Room>> GetActiveRoomsAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Rooms
            .Include(r => r.Participants)
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
        var room = await _context.Rooms.FindAsync(new object[] { id }, cancellationToken);
        if (room is not null)
        {
            _context.Rooms.Remove(room);
            await _context.SaveChangesAsync(cancellationToken);
        }
    }
}
