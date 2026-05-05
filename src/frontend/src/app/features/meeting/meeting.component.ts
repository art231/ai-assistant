import { Component, OnInit, OnDestroy } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { Subscription } from 'rxjs';
import { SignalrService, TranscriptMessage, SummaryMessage, TopicChangeMessage, AdviceMessage, AlternativeIdeaMessage, ParticipantInfo } from '../../core/services/signalr.service';
import { ApiService, RoomDto } from '../../core/services/api.service';

@Component({
  selector: 'app-meeting',
  template: `
    <div class="meeting-container">
      <!-- Join/Create Room -->
      <div class="card" *ngIf="!currentRoomId">
        <h2>Join or Create a Meeting</h2>
        <div class="form-group">
          <input
            type="text"
            [(ngModel)]="userName"
            placeholder="Your name"
            class="form-input"
          />
        </div>
        <div class="form-group">
          <input
            type="text"
            [(ngModel)]="roomName"
            placeholder="Room name"
            class="form-input"
          />
        </div>
        <div class="button-group">
          <button class="btn btn-primary" (click)="createRoom()" [disabled]="!roomName || !userName">
            Create Room
          </button>
        </div>

        <h3>Available Rooms</h3>
        <div class="rooms-list">
          <div *ngFor="let room of rooms" class="room-item" (click)="joinRoom(room.id)">
            <span class="room-name">{{ room.name }}</span>
            <span class="room-info">{{ room.participantCount }} participants</span>
            <span class="room-status" [class.active]="room.status === 'Active'">
              {{ room.status }}
            </span>
          </div>
          <div *ngIf="rooms.length === 0" class="empty-state">
            No active rooms. Create one!
          </div>
        </div>
      </div>

      <!-- Active Meeting -->
      <div class="meeting-layout" *ngIf="currentRoomId">
        <!-- Participants Panel -->
        <div class="participants-panel card">
          <h3>Participants ({{ participants.length }})</h3>
          <div class="participants-list">
            <div *ngFor="let p of participants" class="participant-item">
              <span class="participant-name">{{ p.userName }}</span>
              <span class="participant-status" [class.muted]="p.isMuted">
                {{ p.isMuted ? 'Muted' : 'Active' }}
              </span>
            </div>
          </div>
          <div class="meeting-controls">
            <button
              class="btn btn-danger"
              (click)="leaveRoom()"
            >
              Leave Room
            </button>
            <button
              class="btn btn-secondary"
              (click)="isRecording ? stopRecording() : startRecording()"
            >
              {{ isRecording ? 'Stop Recording' : 'Start Recording' }}
            </button>
          </div>
        </div>

        <!-- Transcripts Panel -->
        <div class="transcripts-panel card">
          <h3>Live Transcript</h3>
          <div class="transcripts-list" #transcriptList>
            <div *ngFor="let t of transcripts" class="transcript-item">
              <span class="transcript-user">{{ t.userName }}</span>
              <span class="transcript-text">{{ t.text }}</span>
              <span class="transcript-time">{{ t.timestamp | date:'HH:mm:ss' }}</span>
            </div>
          </div>
        </div>

        <!-- AI Insights Panel -->
        <div class="insights-panel card">
          <h3>AI Insights</h3>
          <div class="insights-list">
            <!-- Summary -->
            <div *ngIf="currentSummary" class="insight-item summary">
              <strong>Summary:</strong>
              <p>{{ currentSummary.summary }}</p>
            </div>

            <!-- Topic Change -->
            <div *ngIf="currentTopicChange" class="insight-item topic-change">
              <strong>Topic Changed:</strong>
              <p>{{ currentTopicChange.newTopic }}</p>
            </div>

            <!-- Advice -->
            <div *ngIf="currentAdvice" class="insight-item advice">
              <strong>Advice:</strong>
              <p>{{ currentAdvice.advice }}</p>
            </div>

            <!-- Alternative Idea -->
            <div *ngIf="currentAlternativeIdea" class="insight-item idea">
              <strong>Alternative Idea:</strong>
              <p>{{ currentAlternativeIdea.idea }}</p>
            </div>
          </div>
        </div>
      </div>
    </div>
  `,
  styles: [
    `
    .meeting-container {
      max-width: 1200px;
      margin: 0 auto;
    }

    .form-group {
      margin-bottom: 12px;
    }

    .form-input {
      width: 100%;
      max-width: 400px;
    }

    .button-group {
      display: flex;
      gap: 8px;
      margin-bottom: 24px;
    }

    .rooms-list {
      margin-top: 12px;
    }

    .room-item {
      display: flex;
      align-items: center;
      justify-content: space-between;
      padding: 12px;
      background-color: #1a1a2e;
      border-radius: 8px;
      margin-bottom: 8px;
      cursor: pointer;
      transition: background-color 0.2s;
    }

    .room-item:hover {
      background-color: #0f3460;
    }

    .room-name {
      font-weight: 600;
    }

    .room-info {
      color: #888;
      font-size: 12px;
    }

    .room-status {
      padding: 4px 8px;
      border-radius: 4px;
      font-size: 11px;
      background-color: #333;
    }

    .room-status.active {
      background-color: #1a8a3f;
    }

    .empty-state {
      color: #666;
      text-align: center;
      padding: 20px;
    }

    .meeting-layout {
      display: grid;
      grid-template-columns: 280px 1fr 320px;
      gap: 16px;
      height: calc(100vh - 120px);
    }

    .participants-panel,
    .transcripts-panel,
    .insights-panel {
      display: flex;
      flex-direction: column;
      overflow: hidden;
    }

    .participants-list,
    .transcripts-list,
    .insights-list {
      flex: 1;
      overflow-y: auto;
    }

    .participant-item {
      display: flex;
      justify-content: space-between;
      align-items: center;
      padding: 8px 0;
      border-bottom: 1px solid #1a1a2e;
    }

    .participant-name {
      font-weight: 500;
    }

    .participant-status {
      font-size: 11px;
      padding: 2px 6px;
      border-radius: 4px;
      background-color: #1a8a3f;
    }

    .participant-status.muted {
      background-color: #666;
    }

    .meeting-controls {
      display: flex;
      gap: 8px;
      margin-top: 16px;
    }

    .transcript-item {
      padding: 8px 0;
      border-bottom: 1px solid #1a1a2e;
    }

    .transcript-user {
      font-weight: 600;
      color: #533483;
      margin-right: 8px;
    }

    .transcript-text {
      color: #ccc;
    }

    .transcript-time {
      display: block;
      font-size: 11px;
      color: #666;
      margin-top: 2px;
    }

    .insight-item {
      padding: 12px;
      margin-bottom: 8px;
      border-radius: 8px;
      font-size: 13px;
    }

    .insight-item.summary {
      background-color: #0f3460;
    }

    .insight-item.topic-change {
      background-color: #533483;
    }

    .insight-item.advice {
      background-color: #1a8a3f;
    }

    .insight-item.idea {
      background-color: #e94560;
    }

    .insight-item strong {
      display: block;
      margin-bottom: 4px;
    }

    .insight-item p {
      margin: 0;
      line-height: 1.4;
    }
    `,
  ],
})
export class MeetingComponent implements OnInit, OnDestroy {
  userName = '';
  roomName = '';
  currentRoomId: string | null = null;
  isRecording = false;

  rooms: RoomDto[] = [];
  participants: ParticipantInfo[] = [];
  transcripts: TranscriptMessage[] = [];
  currentSummary: SummaryMessage | null = null;
  currentTopicChange: TopicChangeMessage | null = null;
  currentAdvice: AdviceMessage | null = null;
  currentAlternativeIdea: AlternativeIdeaMessage | null = null;
  recordingError: string | null = null;

  private subscriptions: Subscription[] = [];

  constructor(
    private signalrService: SignalrService,
    private apiService: ApiService,
    private route: ActivatedRoute,
    private router: Router
  ) {}

  ngOnInit(): void {
    this.loadRooms();

    // Check if roomId in URL
    this.route.params.subscribe((params) => {
      if (params['roomId']) {
        this.currentRoomId = params['roomId'];
        this.joinRoom(params['roomId']);
      }
    });

    // Subscribe to SignalR events
    this.subscriptions.push(
      this.signalrService.transcripts$.subscribe((t) => (this.transcripts = t)),
      this.signalrService.summary$.subscribe((s) => (this.currentSummary = s)),
      this.signalrService.topicChange$.subscribe((t) => (this.currentTopicChange = t)),
      this.signalrService.advice$.subscribe((a) => (this.currentAdvice = a)),
      this.signalrService.alternativeIdea$.subscribe((i) => (this.currentAlternativeIdea = i)),
      this.signalrService.participants$.subscribe((p) => (this.participants = p)),
      this.signalrService.recordingStatus$.subscribe((s) => (this.isRecording = s)),
      this.signalrService.recordingError$.subscribe((e) => (this.recordingError = e))
    );
  }

  ngOnDestroy(): void {
    if (this.currentRoomId) {
      this.signalrService.leaveRoom(this.currentRoomId);
    }
    this.subscriptions.forEach((s) => s.unsubscribe());
  }

  loadRooms(): void {
    this.apiService.getRooms().subscribe({
      next: (rooms) => (this.rooms = rooms),
      error: (err) => console.error('Failed to load rooms:', err),
    });
  }

  async createRoom(): Promise<void> {
    try {
      const room = await this.apiService.createRoom({
        name: this.roomName,
        maxParticipants: 20,
      }).toPromise();

      if (room) {
        this.currentRoomId = room.id;
        await this.signalrService.joinRoom(room.id, this.userName);
        this.router.navigate(['/meeting', room.id]);
      }
    } catch (err) {
      console.error('Failed to create room:', err);
    }
  }

  async joinRoom(roomId: string): Promise<void> {
    try {
      this.currentRoomId = roomId;
      await this.signalrService.joinRoom(roomId, this.userName || 'Anonymous');
      this.router.navigate(['/meeting', roomId]);
    } catch (err) {
      console.error('Failed to join room:', err);
    }
  }

  async leaveRoom(): Promise<void> {
    if (this.currentRoomId) {
      await this.signalrService.leaveRoom(this.currentRoomId);
      this.currentRoomId = null;
      this.transcripts = [];
      this.currentSummary = null;
      this.currentTopicChange = null;
      this.currentAdvice = null;
      this.currentAlternativeIdea = null;
      this.router.navigate(['/meeting']);
    }
  }

  async startRecording(): Promise<void> {
    if (this.currentRoomId) {
      await this.signalrService.startRecording(this.currentRoomId);
      this.isRecording = true;
    }
  }

  async stopRecording(): Promise<void> {
    if (this.currentRoomId) {
      await this.signalrService.stopRecording(this.currentRoomId);
      this.isRecording = false;
    }
  }
}
