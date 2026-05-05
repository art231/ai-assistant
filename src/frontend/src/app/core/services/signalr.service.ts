import { Injectable } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { BehaviorSubject, Observable } from 'rxjs';
import { environment } from '../../../environments/environment';

export interface TranscriptMessage {
  roomId: string;
  participantId: string;
  userName: string;
  text: string;
  isFinal: boolean;
  language: string;
  timestamp: number;
}

export interface SummaryMessage {
  roomId: string;
  summary: string;
  timestamp: number;
}

export interface TopicChangeMessage {
  roomId: string;
  newTopic: string;
  timestamp: number;
}

export interface AdviceMessage {
  roomId: string;
  advice: string;
  timestamp: number;
}

export interface AlternativeIdeaMessage {
  roomId: string;
  idea: string;
  timestamp: number;
}

export interface ParticipantInfo {
  participantId: string;
  userName: string;
  isMuted: boolean;
  joinedAt: number;
}

export interface RecordingStatusMessage {
  roomId: string;
  recordingId: string;
  startedAt: number;
}

export interface RecordingStoppedMessage {
  roomId: string;
  recordingId: string;
  durationSeconds: number;
  endedAt: number;
}

export interface RecordingErrorMessage {
  roomId: string;
  error: string;
}

@Injectable({
  providedIn: 'root',
})
export class SignalrService {
  private hubConnection!: signalR.HubConnection;
  private connectionId: string | null = null;

  private transcriptsSubject = new BehaviorSubject<TranscriptMessage[]>([]);
  private summarySubject = new BehaviorSubject<SummaryMessage | null>(null);
  private topicChangeSubject = new BehaviorSubject<TopicChangeMessage | null>(null);
  private adviceSubject = new BehaviorSubject<AdviceMessage | null>(null);
  private alternativeIdeaSubject = new BehaviorSubject<AlternativeIdeaMessage | null>(null);
  private participantsSubject = new BehaviorSubject<ParticipantInfo[]>([]);
  private recordingStatusSubject = new BehaviorSubject<boolean>(false);
  private recordingErrorSubject = new BehaviorSubject<string | null>(null);
  private connectedSubject = new BehaviorSubject<boolean>(false);

  transcripts$ = this.transcriptsSubject.asObservable();
  summary$ = this.summarySubject.asObservable();
  topicChange$ = this.topicChangeSubject.asObservable();
  advice$ = this.adviceSubject.asObservable();
  alternativeIdea$ = this.alternativeIdeaSubject.asObservable();
  participants$ = this.participantsSubject.asObservable();
  recordingStatus$ = this.recordingStatusSubject.asObservable();
  recordingError$ = this.recordingErrorSubject.asObservable();
  connected$ = this.connectedSubject.asObservable();

  constructor() {}

  async startConnection(): Promise<void> {
    this.hubConnection = new signalR.HubConnectionBuilder()
      .withUrl(environment.signalrUrl)
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .build();

    this.registerHandlers();

    try {
      await this.hubConnection.start();
      this.connectionId = this.hubConnection.connectionId;
      this.connectedSubject.next(true);
      console.log('SignalR connected:', this.connectionId);
    } catch (err) {
      console.error('SignalR connection error:', err);
      this.connectedSubject.next(false);
      setTimeout(() => this.startConnection(), 5000);
    }

    this.hubConnection.onreconnecting(() => {
      console.log('SignalR reconnecting...');
      this.connectedSubject.next(false);
    });

    this.hubConnection.onreconnected((connectionId) => {
      this.connectionId = connectionId ?? null;
      this.connectedSubject.next(true);
      console.log('SignalR reconnected:', connectionId);
    });

    this.hubConnection.onclose(() => {
      this.connectedSubject.next(false);
      console.log('SignalR connection closed');
      setTimeout(() => this.startConnection(), 5000);
    });
  }

  private registerHandlers(): void {
    this.hubConnection.on('TranscriptReceived', (message: TranscriptMessage) => {
      const current = this.transcriptsSubject.value;
      this.transcriptsSubject.next([...current, message]);
    });

    this.hubConnection.on('SummaryGenerated', (message: SummaryMessage) => {
      this.summarySubject.next(message);
    });

    this.hubConnection.on('TopicChanged', (message: TopicChangeMessage) => {
      this.topicChangeSubject.next(message);
    });

    this.hubConnection.on('AdviceGenerated', (message: AdviceMessage) => {
      this.adviceSubject.next(message);
    });

    this.hubConnection.on('AlternativeIdea', (message: AlternativeIdeaMessage) => {
      this.alternativeIdeaSubject.next(message);
    });

    this.hubConnection.on('ParticipantsUpdated', (participants: ParticipantInfo[]) => {
      this.participantsSubject.next(participants);
    });

    this.hubConnection.on('RecordingStarted', (message: RecordingStatusMessage) => {
      this.recordingStatusSubject.next(true);
      this.recordingErrorSubject.next(null);
      console.log('Recording started:', message);
    });

    this.hubConnection.on('RecordingStopped', (message: RecordingStoppedMessage) => {
      this.recordingStatusSubject.next(false);
      console.log('Recording stopped:', message);
    });

    this.hubConnection.on('RecordingError', (message: RecordingErrorMessage) => {
      this.recordingErrorSubject.next(message.error);
      console.error('Recording error:', message.error);
    });
  }

  async joinRoom(roomId: string, userName: string): Promise<void> {
    await this.hubConnection.invoke('JoinRoom', roomId, userName);
  }

  async leaveRoom(roomId: string): Promise<void> {
    await this.hubConnection.invoke('LeaveRoom', roomId);
  }

  async startRecording(roomId: string): Promise<void> {
    this.recordingErrorSubject.next(null);
    await this.hubConnection.invoke('StartRecording', roomId);
  }

  async stopRecording(roomId: string): Promise<void> {
    await this.hubConnection.invoke('StopRecording', roomId);
  }

  async getRecordingStatus(roomId: string): Promise<boolean> {
    return await this.hubConnection.invoke('GetRecordingStatus', roomId);
  }

  async sendMessage(roomId: string, message: string): Promise<void> {
    await this.hubConnection.invoke('SendMessage', roomId, message);
  }

  getConnectionId(): string | null {
    return this.connectionId;
  }

  stopConnection(): void {
    if (this.hubConnection) {
      this.hubConnection.stop();
    }
  }
}
