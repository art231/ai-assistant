namespace VoiceChatAI.Domain.Entities;

/// <summary>
/// Represents a meeting room where participants can join and communicate.
/// </summary>
public class Room
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = null!;
    public RoomStatus Status { get; private set; }
    public int MaxParticipants { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? EndedAt { get; private set; }
    public string? Metadata { get; private set; }

    private readonly List<Participant> _participants = new();
    public IReadOnlyCollection<Participant> Participants => _participants.AsReadOnly();

    private Room() { } // EF Core

    public Room(string name, int maxParticipants = 20)
    {
        Id = Guid.NewGuid();
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Status = RoomStatus.Waiting;
        MaxParticipants = maxParticipants;
        CreatedAt = DateTime.UtcNow;
    }

    public Participant AddParticipant(string userName)
    {
        if (_participants.Any(p => p.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Participant '{userName}' is already in the room.");

        if (_participants.Count >= MaxParticipants)
            throw new InvalidOperationException($"Room is full. Maximum {MaxParticipants} participants allowed.");

        if (Status != RoomStatus.Waiting && Status != RoomStatus.Active)
            throw new InvalidOperationException("Cannot join a room that has ended.");

        var participant = new Participant(Id, userName);
        _participants.Add(participant);

        if (Status == RoomStatus.Waiting)
            Status = RoomStatus.Active;

        return participant;
    }

    public void RemoveParticipant(Guid participantId)
    {
        var participant = _participants.FirstOrDefault(p => p.Id == participantId);
        if (participant == null)
            throw new InvalidOperationException("Participant not found.");

        participant.MarkLeft();
        
        if (_participants.Count == 0)
            End();
    }

    public void End()
    {
        Status = RoomStatus.Ended;
        EndedAt = DateTime.UtcNow;

        foreach (var participant in _participants.Where(p => p.LeftAt == null))
            participant.MarkLeft();
    }
}

public enum RoomStatus
{
    Waiting,
    Active,
    Ended
}
