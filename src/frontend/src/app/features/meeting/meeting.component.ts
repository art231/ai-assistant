import { Component, OnInit, OnDestroy, ViewChild, ElementRef } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { Subscription, firstValueFrom } from 'rxjs';
import { SignalrService, TranscriptMessage, SummaryMessage, TopicChangeMessage, AdviceMessage, AlternativeIdeaMessage, ParticipantInfo, SpeakerAnalysisMessage, VoiceMetricsDto } from '../../core/services/signalr.service';
import { ApiService, RoomDto } from '../../core/services/api.service';
import { MicrophoneService, MicrophoneState, MicrophoneDevice } from '../../core/services/microphone.service';
import { MediasoupService, MediasoupTransportOptions } from '../../core/services/mediasoup.service';
import { RecordingService, RecordingState, RecordingResult } from '../../core/services/recording.service';

@Component({
  selector: 'app-meeting',
  template: `
    <div class="meeting-container">
      <!-- Connection Status -->
      <div class="connection-banner" *ngIf="!isConnected && !currentRoomId">
        <span>Connecting to server...</span>
      </div>
      <div class="connection-banner error" *ngIf="connectionError">
        <span>{{ connectionError }}</span>
      </div>

      <!-- Join/Create Room -->
      <div class="card" *ngIf="!currentRoomId">
        <h2>Join or Create a Meeting</h2>
        <div class="form-group">
          <input
            type="text"
            [(ngModel)]="userName"
            placeholder="Your name (optional)"
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
          <button
            class="btn btn-primary"
            (click)="createRoom()"
            [disabled]="!roomName || isLoading"
          >
            {{ isLoading ? 'Creating...' : 'Create Room' }}
          </button>
        </div>

        <h3>Available Rooms</h3>
        <div class="rooms-list">
          <div *ngFor="let room of rooms" class="room-item">
            <div class="room-info-left" (click)="joinRoom(room.id)">
              <span class="room-name">{{ room.name }}</span>
              <span class="room-info">{{ room.participantCount }} participants</span>
              <span class="room-status" [class.active]="room.status === 'Active'">
                {{ room.status }}
              </span>
            </div>
            <button
              class="btn btn-danger btn-sm btn-delete"
              (click)="deleteRoom(room.id)"
              [disabled]="isLoading"
              title="Delete room"
            >
              Delete
            </button>
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

          <!-- Microphone Controls -->
          <div class="mic-section">
            <div class="mic-status-bar">
              <!-- Microphone State Indicator -->
              <div class="mic-indicator" [class]="'mic-' + micState">
                <span class="mic-icon">
                  {{ micState === 'active' ? (isMicMuted ? '🔇' : '🎤') : '🎤' }}
                </span>
                <span class="mic-label">{{ micStateLabel }}</span>
              </div>

              <!-- Audio Level Meter -->
              <div class="audio-level-meter" *ngIf="micState === 'active'">
                <div class="level-bar-container">
                  <div
                    class="level-bar"
                    [style.width.%]="audioLevel * 100"
                    [class.speaking]="audioLevel > 0.1"
                  ></div>
                </div>
              </div>
            </div>

            <!-- Microphone Actions -->
            <div class="mic-actions">
              <!-- Request Mic Access -->
              <button
                *ngIf="micState === 'permission-required' || micState === 'unavailable'"
                class="btn btn-secondary btn-sm"
                (click)="requestMicrophoneAccess()"
                [disabled]="isMicLoading"
              >
                {{ isMicLoading ? 'Requesting...' : 'Enable Microphone' }}
              </button>

              <!-- Mute/Unmute Toggle -->
              <button
                *ngIf="micState === 'active'"
                class="btn btn-sm"
                [class.btn-danger]="!isMicMuted"
                [class.btn-secondary]="isMicMuted"
                (click)="toggleMicrophone()"
              >
                {{ isMicMuted ? 'Unmute' : 'Mute' }}
              </button>

              <!-- Select Microphone Device -->
              <select
                *ngIf="micDevices.length > 1"
                class="form-input form-input-sm"
                (change)="onMicDeviceChange($event)"
                [value]="selectedMicDeviceId"
              >
                <option *ngFor="let device of micDevices" [value]="device.deviceId">
                  {{ device.label }}
                </option>
              </select>
            </div>

            <!-- Mic Error -->
            <div class="error-message" *ngIf="micError">
              {{ micError }}
            </div>
          </div>

          <!-- Local Recording Controls (MediaRecorder) -->
          <div class="recording-section">
            <div class="recording-status-bar">
              <div class="recording-indicator" [class]="'rec-' + localRecordingState">
                <span class="rec-icon">
                  {{ localRecordingState === 'recording' ? '🔴' : '⏺️' }}
                </span>
                <span class="rec-label">{{ localRecordingStateLabel }}</span>
              </div>
              <div class="recording-duration" *ngIf="localRecordingState === 'recording'">
                {{ formatDuration(localRecordingDuration) }}
              </div>
            </div>

            <div class="recording-actions">
              <!-- Start Local Recording -->
              <button
                *ngIf="localRecordingState === 'idle' || localRecordingState === 'completed'"
                class="btn btn-danger btn-sm"
                (click)="startLocalRecording()"
                [disabled]="micState !== 'active'"
                [title]="micState !== 'active' ? 'Enable microphone first' : 'Start recording audio'"
              >
                {{ localRecordingState === 'completed' ? 'Record Again' : '🎙️ Record' }}
              </button>

              <!-- Stop Local Recording -->
              <button
                *ngIf="localRecordingState === 'recording'"
                class="btn btn-secondary btn-sm"
                (click)="stopLocalRecording()"
              >
                ⏹️ Stop & Upload
              </button>

              <!-- Upload Status -->
              <span *ngIf="localRecordingState === 'uploading'" class="upload-status">
                ⬆️ Uploading...
              </span>
              <span *ngIf="localRecordingState === 'completed' && localRecordingResult" class="upload-status success">
                ✅ Uploaded ({{ (localRecordingResult.fileSizeBytes / 1024).toFixed(1) }} KB)
              </span>
            </div>

            <!-- Recording Error -->
            <div class="error-message" *ngIf="localRecordingError">
              {{ localRecordingError }}
            </div>
          </div>

          <!-- Meeting Controls -->
          <div class="meeting-controls">
            <button
              class="btn btn-danger"
              (click)="leaveRoom()"
              [disabled]="isLoading"
            >
              {{ isLoading ? 'Leaving...' : 'Leave Room' }}
            </button>
            <button
              class="btn btn-primary"
              (click)="downloadPdf()"
              [disabled]="isPdfLoading"
              title="Скачать PDF-отчёт встречи"
            >
              {{ isPdfLoading ? '⏳ PDF...' : '📄 Скачать PDF' }}
            </button>
          </div>
          <div class="error-message" *ngIf="actionError">
            {{ actionError }}
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
            <!-- Speaker Analysis -->
            <div *ngIf="currentSpeakerAnalysis" class="insight-section">
              <div class="section-header">
                <span class="section-icon">👥</span>
                <strong>Speaker Analysis</strong>
              </div>
              <div class="speaker-cards">
                <div *ngFor="let s of currentSpeakerAnalysis.speakers" class="speaker-card">
                  <div class="speaker-header">
                    <span class="speaker-id">{{ s.id }}</span>
                    <span class="speaker-gender" [class.male]="s.gender === 'male'" [class.female]="s.gender === 'female'">
                      {{ s.gender === 'male' ? '♂' : '♀' }}
                    </span>
                  </div>
                  <div class="fatigue-bar-container">
                    <div class="fatigue-label">Fatigue</div>
                    <div class="fatigue-bar">
                      <div
                        class="fatigue-fill"
                        [style.width.%]="s.fatigueLevel * 100"
                        [class.low]="s.fatigueLevel < 0.3"
                        [class.medium]="s.fatigueLevel >= 0.3 && s.fatigueLevel < 0.6"
                        [class.high]="s.fatigueLevel >= 0.6"
                      ></div>
                    </div>
                    <span class="fatigue-value">{{ (s.fatigueLevel * 100).toFixed(0) }}%</span>
                  </div>
                </div>
              </div>
              <!-- Break Recommendation -->
              <div *ngIf="currentSpeakerAnalysis.needsBreak" class="recommendation break-recommendation">
                <span class="rec-icon">⚠️</span>
                <div class="rec-text">
                  <strong>Break Recommended!</strong>
                  <p>{{ currentSpeakerAnalysis.breakReason }}</p>
                </div>
              </div>
              <!-- Postpone Recommendation -->
              <div *ngIf="currentSpeakerAnalysis.shouldPostpone" class="recommendation postpone-recommendation">
                <span class="rec-icon">🔄</span>
                <div class="rec-text">
                  <strong>Consider Postponing</strong>
                  <p>{{ currentSpeakerAnalysis.postponeReason }}</p>
                </div>
              </div>
            </div>

            <!-- Summary -->
            <div *ngIf="currentSummary" class="insight-item summary">
              <div class="section-header">
                <span class="section-icon">📝</span>
                <strong>Meeting Summary</strong>
              </div>
              <p>{{ currentSummary.summary }}</p>
            </div>

            <!-- Topic Change -->
            <div *ngIf="currentTopicChange" class="insight-item topic-change">
              <div class="section-header">
                <span class="section-icon">🔄</span>
                <strong>Topic Changed</strong>
              </div>
              <p>{{ currentTopicChange.newTopic }}</p>
            </div>

            <!-- Advice -->
            <div *ngIf="currentAdvice" class="insight-item advice">
              <div class="section-header">
                <span class="section-icon">💡</span>
                <strong>Advice</strong>
              </div>
              <p>{{ currentAdvice.advice }}</p>
            </div>

            <!-- Alternative Idea -->
            <div *ngIf="currentAlternativeIdea" class="insight-item idea">
              <div class="section-header">
                <span class="section-icon">🧠</span>
                <strong>Alternative Idea</strong>
              </div>
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

    .connection-banner {
      background-color: #0f3460;
      color: #e0e0e0;
      padding: 8px 16px;
      border-radius: 8px;
      margin-bottom: 16px;
      text-align: center;
      font-size: 13px;
    }

    .connection-banner.error {
      background-color: #5c1a1a;
      color: #ff6b6b;
    }

    .form-group {
      margin-bottom: 12px;
    }

    .form-input {
      width: 100%;
      max-width: 400px;
    }

    .form-input-sm {
      max-width: 200px;
      font-size: 12px;
      padding: 4px 8px;
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
      transition: background-color 0.2s;
    }

    .room-item:hover {
      background-color: #0f3460;
    }

    .room-info-left {
      display: flex;
      align-items: center;
      gap: 12px;
      flex: 1;
      cursor: pointer;
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

    .btn-delete {
      margin-left: 12px;
      flex-shrink: 0;
    }

    .empty-state {
      color: #666;
      text-align: center;
      padding: 20px;
    }

    .meeting-layout {
      display: grid;
      grid-template-columns: 300px 1fr 320px;
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

    /* Microphone Section */
    .mic-section {
      margin-top: 12px;
      padding: 12px;
      background-color: #1a1a2e;
      border-radius: 8px;
    }

    .mic-status-bar {
      display: flex;
      align-items: center;
      gap: 8px;
      margin-bottom: 8px;
    }

    .mic-indicator {
      display: flex;
      align-items: center;
      gap: 6px;
      font-size: 12px;
      padding: 4px 8px;
      border-radius: 4px;
      background-color: #333;
    }

    .mic-indicator.mic-active {
      background-color: #1a8a3f;
    }

    .mic-indicator.mic-permission-required {
      background-color: #b8860b;
    }

    .mic-indicator.mic-unavailable {
      background-color: #5c1a1a;
    }

    .mic-indicator.mic-error {
      background-color: #5c1a1a;
    }

    .mic-icon {
      font-size: 14px;
    }

    .mic-label {
      white-space: nowrap;
    }

    .audio-level-meter {
      flex: 1;
    }

    .level-bar-container {
      height: 6px;
      background-color: #333;
      border-radius: 3px;
      overflow: hidden;
    }

    .level-bar {
      height: 100%;
      background-color: #533483;
      border-radius: 3px;
      transition: width 0.1s ease;
    }

    .level-bar.speaking {
      background-color: #1a8a3f;
    }

    .mic-actions {
      display: flex;
      gap: 6px;
      align-items: center;
      flex-wrap: wrap;
    }

    .btn-sm {
      padding: 4px 12px;
      font-size: 12px;
    }

    .meeting-controls {
      display: flex;
      gap: 8px;
      margin-top: 16px;
    }

    /* Recording Section */
    .recording-section {
      margin-top: 12px;
      padding: 12px;
      background-color: #1a1a2e;
      border-radius: 8px;
    }

    .recording-status-bar {
      display: flex;
      align-items: center;
      gap: 8px;
      margin-bottom: 8px;
    }

    .recording-indicator {
      display: flex;
      align-items: center;
      gap: 6px;
      font-size: 12px;
      padding: 4px 8px;
      border-radius: 4px;
      background-color: #333;
    }

    .recording-indicator.rec-recording {
      background-color: #5c1a1a;
      animation: pulse 1.5s infinite;
    }

    .recording-indicator.rec-uploading {
      background-color: #b8860b;
    }

    .recording-indicator.rec-completed {
      background-color: #1a8a3f;
    }

    .recording-indicator.rec-error {
      background-color: #5c1a1a;
    }

    @keyframes pulse {
      0%, 100% { opacity: 1; }
      50% { opacity: 0.6; }
    }

    .rec-icon {
      font-size: 14px;
    }

    .rec-label {
      white-space: nowrap;
    }

    .recording-duration {
      font-size: 14px;
      font-weight: 600;
      color: #e94560;
      font-family: monospace;
    }

    .recording-actions {
      display: flex;
      gap: 6px;
      align-items: center;
      flex-wrap: wrap;
    }

    .upload-status {
      font-size: 12px;
      color: #b8860b;
      padding: 2px 8px;
      border-radius: 4px;
      background-color: rgba(184, 134, 11, 0.2);
    }

    .upload-status.success {
      color: #4caf50;
      background-color: rgba(76, 175, 80, 0.2);
    }

    .error-message {
      margin-top: 8px;
      padding: 8px 12px;
      background-color: #5c1a1a;
      color: #ff6b6b;
      border-radius: 6px;
      font-size: 12px;
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

    .insight-section {
      padding: 12px;
      margin-bottom: 8px;
      border-radius: 8px;
      background-color: #16213e;
      font-size: 13px;
    }

    .section-header {
      display: flex;
      align-items: center;
      gap: 6px;
      margin-bottom: 8px;
    }

    .section-icon {
      font-size: 16px;
    }

    .section-header strong {
      font-size: 13px;
      color: #e0e0e0;
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

    /* Speaker Cards */
    .speaker-cards {
      display: flex;
      flex-direction: column;
      gap: 8px;
      margin-bottom: 8px;
    }

    .speaker-card {
      display: flex;
      flex-direction: column;
      gap: 4px;
      padding: 8px 10px;
      background-color: #1a1a2e;
      border-radius: 6px;
    }

    .speaker-header {
      display: flex;
      align-items: center;
      justify-content: space-between;
    }

    .speaker-id {
      font-weight: 600;
      font-size: 12px;
      color: #ccc;
    }

    .speaker-gender {
      font-size: 14px;
      padding: 2px 6px;
      border-radius: 4px;
    }

    .speaker-gender.male {
      background-color: #0f3460;
    }

    .speaker-gender.female {
      background-color: #533483;
    }

    .fatigue-bar-container {
      display: flex;
      align-items: center;
      gap: 6px;
    }

    .fatigue-label {
      font-size: 10px;
      color: #888;
      min-width: 40px;
    }

    .fatigue-bar {
      flex: 1;
      height: 6px;
      background-color: #333;
      border-radius: 3px;
      overflow: hidden;
    }

    .fatigue-fill {
      height: 100%;
      border-radius: 3px;
      transition: width 0.5s ease;
    }

    .fatigue-fill.low {
      background-color: #1a8a3f;
    }

    .fatigue-fill.medium {
      background-color: #b8860b;
    }

    .fatigue-fill.high {
      background-color: #e94560;
    }

    .fatigue-value {
      font-size: 10px;
      color: #888;
      min-width: 30px;
      text-align: right;
    }

    /* Recommendations */
    .recommendation {
      display: flex;
      gap: 8px;
      padding: 8px 10px;
      border-radius: 6px;
      margin-top: 8px;
      align-items: flex-start;
    }

    .recommendation.break-recommendation {
      background-color: rgba(233, 69, 96, 0.2);
      border: 1px solid #e94560;
    }

    .recommendation.postpone-recommendation {
      background-color: rgba(184, 134, 11, 0.2);
      border: 1px solid #b8860b;
    }

    .rec-icon {
      font-size: 18px;
      flex-shrink: 0;
    }

    .rec-text {
      flex: 1;
    }

    .rec-text strong {
      display: block;
      font-size: 12px;
      margin-bottom: 2px;
    }

    .rec-text p {
      margin: 0;
      font-size: 11px;
      color: #ccc;
      line-height: 1.3;
    }
    `,
  ],
})
export class MeetingComponent implements OnInit, OnDestroy {
  @ViewChild('transcriptList') transcriptListEl?: ElementRef;

  userName = '';
  roomName = '';
  currentRoomId: string | null = null;
  isRecording = false;
  isLoading = false;
  isConnected = false;
  connectionError: string | null = null;
  actionError: string | null = null;

  // Local recording state (MediaRecorder)
  localRecordingState: RecordingState = 'idle';
  localRecordingDuration: number = 0;
  localRecordingResult: RecordingResult | null = null;
  localRecordingError: string | null = null;

  rooms: RoomDto[] = [];
  participants: ParticipantInfo[] = [];
  transcripts: TranscriptMessage[] = [];
  currentSummary: SummaryMessage | null = null;
  currentTopicChange: TopicChangeMessage | null = null;
  currentAdvice: AdviceMessage | null = null;
  currentAlternativeIdea: AlternativeIdeaMessage | null = null;
  currentSpeakerAnalysis: SpeakerAnalysisMessage | null = null;
  recordingError: string | null = null;

  // Microphone state
  micState: MicrophoneState = 'unavailable';
  micDevices: MicrophoneDevice[] = [];
  selectedMicDeviceId: string = '';
  audioLevel: number = 0;
  isMicMuted: boolean = false;
  micError: string | null = null;
  isMicLoading: boolean = false;
  isPdfLoading: boolean = false;

  get micStateLabel(): string {
    switch (this.micState) {
      case 'active': return this.isMicMuted ? 'Muted' : 'Active';
      case 'permission-required': return 'Permission Needed';
      case 'unavailable': return 'No Mic';
      case 'error': return 'Error';
      default: return 'Unknown';
    }
  }

  get localRecordingStateLabel(): string {
    switch (this.localRecordingState) {
      case 'idle': return 'Ready';
      case 'recording': return 'Recording';
      case 'stopping': return 'Processing...';
      case 'uploading': return 'Uploading...';
      case 'completed': return 'Uploaded';
      case 'error': return 'Error';
      default: return 'Ready';
    }
  }

  private subscriptions: Subscription[] = [];
  private participantId: string = '';
  private isLeavingIntentionally = false;

  constructor(
    private signalrService: SignalrService,
    private apiService: ApiService,
    private microphoneService: MicrophoneService,
    private mediasoupService: MediasoupService,
    private recordingService: RecordingService,
    private route: ActivatedRoute,
    private router: Router
  ) {}

  ngOnInit(): void {
    this.loadRooms();

    // Subscribe to connection status
    this.subscriptions.push(
      this.signalrService.connected$.subscribe((connected) => {
        this.isConnected = connected;
        if (connected) {
          this.connectionError = null;
        }
      }),
      this.signalrService.connectionError$.subscribe((err) => {
        if (err) {
          this.connectionError = err;
        }
      })
    );

    // Check if roomId in URL
    this.route.params.subscribe((params) => {
      if (params['roomId']) {
        this.currentRoomId = params['roomId'];
        this.joinRoom(params['roomId']);
      }
    });

    // Subscribe to SignalR events
    this.subscriptions.push(
      this.signalrService.transcripts$.subscribe((t) => {
        this.transcripts = t;
        this.scrollTranscriptsToBottom();
      }),
      this.signalrService.summary$.subscribe((s) => (this.currentSummary = s)),
      this.signalrService.topicChange$.subscribe((t) => (this.currentTopicChange = t)),
      this.signalrService.advice$.subscribe((a) => (this.currentAdvice = a)),
      this.signalrService.alternativeIdea$.subscribe((i) => (this.currentAlternativeIdea = i)),
      this.signalrService.speakerAnalysis$.subscribe((a) => (this.currentSpeakerAnalysis = a)),
      this.signalrService.participants$.subscribe((p) => (this.participants = p)),
      this.signalrService.recordingStatus$.subscribe((s) => (this.isRecording = s)),
      this.signalrService.recordingError$.subscribe((e) => {
        this.recordingError = e;
        if (e) {
          this.actionError = e;
        }
      })
    );

    // Subscribe to microphone state
    this.subscriptions.push(
      this.microphoneService.state$.subscribe((state) => {
        this.micState = state;
      }),
      this.microphoneService.devices$.subscribe((devices) => {
        this.micDevices = devices;
        if (devices.length > 0 && !this.selectedMicDeviceId) {
          this.selectedMicDeviceId = devices[0].deviceId;
        }
      }),
      this.microphoneService.audioLevel$.subscribe((level) => {
        this.audioLevel = level;
      }),
      this.microphoneService.isMuted$.subscribe((muted) => {
        this.isMicMuted = muted;
      }),
      this.microphoneService.error$.subscribe((err) => {
        this.micError = err;
      })
    );

    // Subscribe to recording service state
    this.subscriptions.push(
      this.recordingService.state$.subscribe((state) => {
        this.localRecordingState = state;
      }),
      this.recordingService.duration$.subscribe((duration) => {
        this.localRecordingDuration = duration;
      }),
      this.recordingService.error$.subscribe((err) => {
        this.localRecordingError = err;
        if (err) {
          this.actionError = err;
        }
      }),
      this.recordingService.result$.subscribe((result) => {
        this.localRecordingResult = result;
        if (result) {
          console.log('Recording uploaded successfully:', result);
        }
      })
    );
  }

  ngOnDestroy(): void {
    // Only leave the room if the user intentionally clicked "Leave Room".
    // On page reload or browser back button, we do NOT want to leave the room,
    // so the room stays active and visible in the rooms list.
    if (this.isLeavingIntentionally) {
      // Leave Mediasoup room
      if (this.currentRoomId && this.participantId) {
        this.mediasoupService.leaveRoom(this.currentRoomId, this.participantId).catch(() => {});
      }

      // Leave SignalR room
      if (this.currentRoomId) {
        this.signalrService.leaveRoom(this.currentRoomId).catch(() => {});
      }
    }

    // Always stop microphone and unsubscribe
    this.microphoneService.stopStream();
    this.subscriptions.forEach((s) => s.unsubscribe());
  }

  private scrollTranscriptsToBottom(): void {
    setTimeout(() => {
      if (this.transcriptListEl) {
        this.transcriptListEl.nativeElement.scrollTop = this.transcriptListEl.nativeElement.scrollHeight;
      }
    }, 50);
  }

  loadRooms(): void {
    this.apiService.getRooms().subscribe({
      next: (rooms) => (this.rooms = rooms),
      error: (err) => {
        console.error('Failed to load rooms:', err);
        this.connectionError = 'Failed to load rooms. Is the server running?';
      },
    });
  }

  async createRoom(): Promise<void> {
    this.isLoading = true;
    this.actionError = null;
    this.connectionError = null;

    try {
      // Ensure SignalR is connected first
      await this.signalrService.ensureConnectedAsync();

      // Create room via API
      const room = await firstValueFrom(
        this.apiService.createRoom({
          name: this.roomName,
          maxParticipants: 20,
        })
      );

      if (room) {
        this.currentRoomId = room.id;
        await this.signalrService.joinRoom(room.id, this.userName || 'Гость');
        
        // Create room in Mediasoup (idempotent - safe if already exists)
        await this.mediasoupService.createRoom(room.id);
        
        this.router.navigate(['/meeting', room.id]);
      }
    } catch (err: any) {
      console.error('Failed to create room:', err);
      this.actionError = err?.message || 'Failed to create room. Check console for details.';
      this.currentRoomId = null;
    } finally {
      this.isLoading = false;
    }
  }

  async joinRoom(roomId: string): Promise<void> {
    this.isLoading = true;
    this.actionError = null;
    this.connectionError = null;

    try {
      // Ensure SignalR is connected first
      await this.signalrService.ensureConnectedAsync();

      this.currentRoomId = roomId;
      await this.signalrService.joinRoom(roomId, this.userName || 'Гость');
      
      // Create room in Mediasoup (idempotent - safe if already exists)
      // This ensures the Mediasoup router is ready for this room
      await this.mediasoupService.createRoom(roomId);
      
      this.router.navigate(['/meeting', roomId]);
    } catch (err: any) {
      console.error('Failed to join room:', err);
      this.actionError = err?.message || 'Failed to join room. Check console for details.';
      this.currentRoomId = null;
    } finally {
      this.isLoading = false;
    }
  }

  async leaveRoom(): Promise<void> {
    if (!this.currentRoomId) return;

    this.isLoading = true;
    this.actionError = null;

    try {
      // Mark that we are intentionally leaving (not a page reload or browser back)
      this.isLeavingIntentionally = true;

      // Leave Mediasoup room
      if (this.participantId) {
        await this.mediasoupService.leaveRoom(this.currentRoomId, this.participantId);
      }

      // Stop microphone
      this.microphoneService.stopStream();

      // Leave SignalR room
      await this.signalrService.ensureConnectedAsync();
      await this.signalrService.leaveRoom(this.currentRoomId);
      this.currentRoomId = null;
      this.participantId = '';
      this.transcripts = [];
      this.currentSummary = null;
      this.currentTopicChange = null;
      this.currentAdvice = null;
      this.currentAlternativeIdea = null;
      this.currentSpeakerAnalysis = null;
      this.actionError = null;
      this.router.navigate(['/meeting']);
    } catch (err: any) {
      console.error('Failed to leave room:', err);
      this.actionError = err?.message || 'Failed to leave room.';
    } finally {
      this.isLoading = false;
    }
  }

  // ─── Room Deletion ───────────────────────────────────────────

  async deleteRoom(roomId: string): Promise<void> {
    const confirmed = confirm('Are you sure you want to delete this room?');
    if (!confirmed) return;

    this.isLoading = true;
    this.actionError = null;

    try {
      await firstValueFrom(this.apiService.closeRoom(roomId));
      this.rooms = this.rooms.filter(r => r.id !== roomId);
    } catch (err: any) {
      console.error('Failed to delete room:', err);
      this.actionError = err?.message || 'Failed to delete room.';
    } finally {
      this.isLoading = false;
    }
  }

  // ─── Microphone Methods ──────────────────────────────────────

  /**
   * Request microphone access from the user.
   */
  async requestMicrophoneAccess(): Promise<void> {
    this.isMicLoading = true;
    this.micError = null;

    try {
      const stream = await this.microphoneService.requestAccess(
        this.selectedMicDeviceId || undefined
      );
      console.log('Microphone access granted, stream ID:', stream.id);

      // If we're in a room, connect to Mediasoup
      if (this.currentRoomId) {
        await this.connectToMediasoup();
      }
    } catch (err: any) {
      console.error('Failed to get microphone access:', err);
      // Error is already set by MicrophoneService
    } finally {
      this.isMicLoading = false;
    }
  }

  /**
   * Toggle microphone mute/unmute.
   */
  toggleMicrophone(): void {
    this.microphoneService.toggleMute();
  }

  /**
   * Handle microphone device selection change.
   */
  async onMicDeviceChange(event: Event): Promise<void> {
    const select = event.target as HTMLSelectElement;
    this.selectedMicDeviceId = select.value;

    // Re-request access with the new device
    if (this.micState === 'active') {
      await this.requestMicrophoneAccess();
    }
  }

  /**
   * Connect to Mediasoup SFU for audio streaming.
   * Uses idempotent createRoom (safe to call if room already exists).
   */
  private async connectToMediasoup(): Promise<void> {
    if (!this.currentRoomId) return;

    try {
      // Generate a participant ID (use SignalR connection ID if available)
      this.participantId = this.signalrService.getConnectionId() || `participant_${Date.now()}`;

      // Create room in Mediasoup (idempotent - returns existing room if already created)
      await this.mediasoupService.createRoom(this.currentRoomId);

      // Join room and get transport options
      const transportOptions = await this.mediasoupService.joinRoom(
        this.currentRoomId,
        this.participantId
      );

      // Start producing audio
      const producerInfo = await this.mediasoupService.produceAudio(
        this.currentRoomId,
        this.participantId,
        transportOptions
      );

      console.log('Connected to Mediasoup, producer:', producerInfo.producerId);
    } catch (err: any) {
      console.error('Failed to connect to Mediasoup:', err);
      this.actionError = `Failed to connect audio: ${err.message}`;
    }
  }

  /**
   * Format seconds to MM:SS display.
   */
  formatDuration(seconds: number): string {
    const mins = Math.floor(seconds / 60);
    const secs = Math.floor(seconds % 60);
    return `${mins.toString().padStart(2, '0')}:${secs.toString().padStart(2, '0')}`;
  }

  // ─── Local Recording Methods (MediaRecorder + HTTP Upload) ──

  /**
   * Start local recording using MediaRecorder API.
   * Records audio from the microphone stream and uploads via HTTP when stopped.
   */
  async startLocalRecording(): Promise<void> {
    if (!this.currentRoomId) {
      this.actionError = 'No active room';
      return;
    }

    const stream = this.microphoneService.getStream();
    if (!stream) {
      this.actionError = 'No microphone stream available. Enable microphone first.';
      return;
    }

    this.localRecordingError = null;
    this.localRecordingResult = null;
    this.actionError = null;

    this.recordingService.startRecording(stream);
  }

  /**
   * Stop local recording and upload the recorded audio to the server.
   */
  async stopLocalRecording(): Promise<void> {
    if (!this.currentRoomId) {
      this.actionError = 'No active room';
      return;
    }

    const audioBlob = this.recordingService.stopRecording();
    if (!audioBlob) {
      this.actionError = 'No audio data recorded';
      return;
    }

    console.log(`Recording stopped: ${audioBlob.size} bytes, type: ${audioBlob.type}`);

    try {
      await this.recordingService.uploadRecording(this.currentRoomId, audioBlob);
      console.log('Recording uploaded successfully');
    } catch (err: any) {
      console.error('Failed to upload recording:', err);
      this.actionError = `Upload failed: ${err.message || err}`;
    }
  }

  // ─── PDF Export ──────────────────────────────────────────────

  /**
   * Download PDF report for the current room.
   */
  async downloadPdf(): Promise<void> {
    if (!this.currentRoomId) {
      this.actionError = 'Нет активной комнаты';
      return;
    }

    this.isPdfLoading = true;
    this.actionError = null;

    try {
      const blob = await firstValueFrom(this.apiService.exportRoomPdf(this.currentRoomId));
      
      // Create download link
      const url = window.URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `отчёт_встречи_${new Date().toISOString().split('T')[0]}.pdf`;
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
      window.URL.revokeObjectURL(url);
      
      console.log('PDF downloaded successfully');
    } catch (err: any) {
      console.error('Failed to download PDF:', err);
      this.actionError = err?.error?.error || err?.message || 'Не удалось скачать PDF';
    } finally {
      this.isPdfLoading = false;
    }
  }
}
