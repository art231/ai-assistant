using VoiceChatAI.Application.DTOs;
using VoiceChatAI.Domain.Entities;
using VoiceChatAI.Domain.Interfaces;

namespace VoiceChatAI.Application.Services;

public class RoomService : IRoomService
{
    private readonly IRoomRepository _roomRepository;
    private readonly ITranscriptRepository _transcriptRepository;

    public RoomService(IRoomRepository roomRepository, ITranscriptRepository transcriptRepository)
    {
        _roomRepository = roomRepository;
        _transcriptRepository = transcriptRepository;
    }

    public async Task<RoomDto> CreateRoomAsync(CreateRoomDto dto, CancellationToken cancellationToken = default)
    {
        var room = new Room(dto.Name, dto.MaxParticipants);
        var created = await _roomRepository.CreateAsync(room, cancellationToken);
        return MapToDto(created);
    }

    public async Task<RoomDto?> GetRoomAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        var room = await _roomRepository.GetByIdAsync(roomId, cancellationToken);
        return room is not null ? MapToDto(room) : null;
    }

    public async Task<IEnumerable<RoomDto>> GetActiveRoomsAsync(CancellationToken cancellationToken = default)
    {
        var rooms = await _roomRepository.GetActiveRoomsAsync(cancellationToken);
        return rooms.Select(MapToDto);
    }

    public async Task<ParticipantDto> JoinRoomAsync(Guid roomId, string userName, CancellationToken cancellationToken = default)
    {
        var room = await _roomRepository.GetByIdAsync(roomId, cancellationToken)
            ?? throw new InvalidOperationException($"Room {roomId} not found.");

        var participant = room.AddParticipant(userName);
        await _roomRepository.UpdateAsync(room, cancellationToken);

        return new ParticipantDto(
            participant.Id,
            participant.UserName,
            participant.JoinedAt,
            participant.IsSpeaking,
            participant.AudioLevel
        );
    }

    public async Task LeaveRoomAsync(Guid roomId, Guid participantId, CancellationToken cancellationToken = default)
    {
        var room = await _roomRepository.GetByIdAsync(roomId, cancellationToken)
            ?? throw new InvalidOperationException($"Room {roomId} not found.");

        room.RemoveParticipant(participantId);
        await _roomRepository.UpdateAsync(room, cancellationToken);
    }

    public async Task EndRoomAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        var room = await _roomRepository.GetByIdAsync(roomId, cancellationToken)
            ?? throw new InvalidOperationException($"Room {roomId} not found.");

        room.End();
        await _roomRepository.UpdateAsync(room, cancellationToken);
    }

    public async Task<IEnumerable<ParticipantDto>> GetParticipantsAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        var room = await _roomRepository.GetByIdAsync(roomId, cancellationToken)
            ?? throw new InvalidOperationException($"Room {roomId} not found.");

        return room.Participants
            .Where(p => p.IsActive)
            .Select(p => new ParticipantDto(
                p.Id,
                p.UserName,
                p.JoinedAt,
                p.IsSpeaking,
                p.AudioLevel
            ));
    }

    private static RoomDto MapToDto(Room room) => new(
        room.Id,
        room.Name,
        room.Status.ToString(),
        room.MaxParticipants,
        room.Participants.Count(p => p.IsActive),
        room.CreatedAt,
        room.EndedAt
    );
}
