using Microsoft.Extensions.Logging;
using VoiceChatAI.Application.DTOs;
using VoiceChatAI.Domain.Entities;
using VoiceChatAI.Domain.Interfaces;

namespace VoiceChatAI.Application.Services;

public class RoomService : IRoomService
{
    private readonly IRoomRepository _roomRepository;
    private readonly ITranscriptRepository _transcriptRepository;
    private readonly ILogger<RoomService> _logger;

    public RoomService(
        IRoomRepository roomRepository,
        ITranscriptRepository transcriptRepository,
        ILogger<RoomService> logger)
    {
        _roomRepository = roomRepository;
        _transcriptRepository = transcriptRepository;
        _logger = logger;
    }

    public async Task<RoomDto> CreateRoomAsync(CreateRoomDto dto, CancellationToken cancellationToken = default)
    {
        var room = new Room(dto.Name, dto.MaxParticipants);
        var created = await _roomRepository.CreateAsync(room, cancellationToken);
        return await MapToDto(created);
    }

    public async Task<RoomDto?> GetRoomAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        var room = await _roomRepository.GetByIdAsync(roomId, cancellationToken);
        return room is not null ? await MapToDto(room) : null;
    }

    public async Task<IEnumerable<RoomDto>> GetActiveRoomsAsync(CancellationToken cancellationToken = default)
    {
        var rooms = await _roomRepository.GetActiveRoomsAsync(cancellationToken);
        var result = new List<RoomDto>();
        foreach (var room in rooms)
            result.Add(await MapToDto(room));
        return result;
    }

    public async Task<ParticipantDto> JoinRoomAsync(Guid roomId, string userName, CancellationToken cancellationToken = default)
    {
        var room = await _roomRepository.GetByIdAsync(roomId, cancellationToken)
            ?? throw new InvalidOperationException($"Room {roomId} not found.");

        // Проверяем, есть ли уже активный участник с таким именем в БД
        var existingParticipants = await _roomRepository.GetActiveParticipantsAsync(roomId, cancellationToken);
        var existingParticipant = existingParticipants.FirstOrDefault(p =>
            p.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase));

        if (existingParticipant != null)
        {
            // Если участник уже есть в комнате — возвращаем его (переиспользуем)
            _logger.LogInformation("Participant {UserName} already in room {RoomId}, reusing existing participant", userName, roomId);
            return new ParticipantDto(
                existingParticipant.Id,
                existingParticipant.UserName,
                existingParticipant.JoinedAt,
                existingParticipant.IsSpeaking,
                existingParticipant.AudioLevel
            );
        }

        // Используем доменный метод AddParticipant, который:
        // - проверяет дубликаты имён (в памяти, после загрузки из БД)
        // - проверяет лимит участников
        // - меняет статус с Waiting на Active при первом участнике
        var participant = room.AddParticipant(userName);

        // Сохраняем нового участника отдельно
        await _roomRepository.AddParticipantAsync(participant, cancellationToken);

        // Обновляем статус комнаты (если изменился с Waiting на Active)
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

        var participant = await _roomRepository.GetParticipantAsync(roomId, participantId, cancellationToken)
            ?? throw new InvalidOperationException("Participant not found.");

        // Помечаем участника как вышедшего напрямую через репозиторий
        await _roomRepository.RemoveParticipantAsync(roomId, participantId, cancellationToken);

        // Проверяем, остались ли ещё активные участники
        var activeCount = await _roomRepository.GetActiveParticipantCountAsync(roomId, cancellationToken);
        if (activeCount == 0)
        {
            room.End();
            await _roomRepository.UpdateAsync(room, cancellationToken);
        }
    }

    public async Task DeleteRoomAsync(Guid roomId, CancellationToken cancellationToken = default)
    {
        await _roomRepository.DeleteAsync(roomId, cancellationToken);
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
        var roomExists = await _roomRepository.GetByIdAsync(roomId, cancellationToken);
        if (roomExists is null)
            throw new InvalidOperationException($"Room {roomId} not found.");

        var participants = await _roomRepository.GetActiveParticipantsAsync(roomId, cancellationToken);

        return participants.Select(p => new ParticipantDto(
            p.Id,
            p.UserName,
            p.JoinedAt,
            p.IsSpeaking,
            p.AudioLevel
        ));
    }

    private async Task<RoomDto> MapToDto(Room room)
    {
        var activeCount = await _roomRepository.GetActiveParticipantCountAsync(room.Id);
        return new RoomDto(
            room.Id,
            room.Name,
            room.Status.ToString(),
            room.MaxParticipants,
            activeCount,
            room.CreatedAt,
            room.EndedAt
        );
    }
}
