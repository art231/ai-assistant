import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../../environments/environment';

export interface RoomDto {
  id: string;
  name: string;
  status: string;
  participantCount: number;
  createdAt: string;
}

export interface CreateRoomDto {
  name: string;
  maxParticipants: number;
}

export interface MeetingRecordingDto {
  id: string;
  roomId: string;
  roomName: string;
  audioPath: string;
  transcript: string;
  startedAt: string;
  endedAt: string | null;
  duration: number;
  durationSeconds: number;
  status: string;
}

export interface SearchResultDto {
  recordingId: string;
  roomName: string;
  transcriptSnippet: string;
  timestamp: number;
  startedAt: string;
}

@Injectable({
  providedIn: 'root',
})
export class ApiService {
  private apiUrl = environment.apiUrl;

  constructor(private http: HttpClient) {}

  // Room management
  createRoom(dto: CreateRoomDto): Observable<RoomDto> {
    return this.http.post<RoomDto>(`${this.apiUrl}/api/rooms`, dto);
  }

  getRooms(): Observable<RoomDto[]> {
    return this.http.get<RoomDto[]>(`${this.apiUrl}/api/rooms`);
  }

  getRoom(id: string): Observable<RoomDto> {
    return this.http.get<RoomDto>(`${this.apiUrl}/api/rooms/${id}`);
  }

  closeRoom(id: string): Observable<void> {
    return this.http.delete<void>(`${this.apiUrl}/api/rooms/${id}`);
  }

  // Meeting recordings
  getRecordings(): Observable<MeetingRecordingDto[]> {
    return this.http.get<MeetingRecordingDto[]>(`${this.apiUrl}/api/recordings`);
  }

  getRecording(id: string): Observable<MeetingRecordingDto> {
    return this.http.get<MeetingRecordingDto>(`${this.apiUrl}/api/recordings/${id}`);
  }

  // Search
  searchTranscripts(query: string): Observable<SearchResultDto[]> {
    const params = new HttpParams().set('q', query);
    return this.http.get<SearchResultDto[]>(`${this.apiUrl}/api/search`, { params });
  }

  // Analytics
  getParticipantAnalytics(participantId: string): Observable<any> {
    return this.http.get(`${this.apiUrl}/api/analytics/participant/${participantId}`);
  }

  getRoomAnalytics(roomId: string): Observable<any> {
    return this.http.get(`${this.apiUrl}/api/analytics/room/${roomId}`);
  }

  getMeetingEfficiency(days: number = 30): Observable<any[]> {
    const params = new HttpParams().set('days', days.toString());
    return this.http.get<any[]>(`${this.apiUrl}/api/analytics/meeting-efficiency`, { params });
  }

  // Room management - end room
  endRoom(roomId: string): Observable<any> {
    return this.http.post(`${this.apiUrl}/api/rooms/${roomId}/end`, {});
  }

  // PDF export
  exportRoomPdf(roomId: string): Observable<Blob> {
    return this.http.get(`${this.apiUrl}/api/analytics/room/${roomId}/export-pdf`, {
      responseType: 'blob'
    });
  }
}
