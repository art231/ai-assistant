import { Injectable, NgZone } from '@angular/core';
import { BehaviorSubject, Observable } from 'rxjs';
import { HttpClient } from '@angular/common/http';
import { environment } from '../../../environments/environment';

export type RecordingState = 'idle' | 'recording' | 'stopping' | 'uploading' | 'completed' | 'error';

export interface RecordingResult {
  id: string;
  roomId: string;
  audioPath: string;
  fileSizeBytes: number;
  status: string;
  startedAt: string;
  endedAt: string;
  message: string;
}

@Injectable({
  providedIn: 'root',
})
export class RecordingService {
  private apiUrl = environment.apiUrl;

  private mediaRecorder: MediaRecorder | null = null;
  private audioChunks: Blob[] = [];
  private recordingStartTime: number = 0;
  private recordingDuration: number = 0;
  private durationInterval: number | null = null;

  private stateSubject = new BehaviorSubject<RecordingState>('idle');
  private durationSubject = new BehaviorSubject<number>(0);
  private errorSubject = new BehaviorSubject<string | null>(null);
  private resultSubject = new BehaviorSubject<RecordingResult | null>(null);

  state$: Observable<RecordingState> = this.stateSubject.asObservable();
  duration$: Observable<number> = this.durationSubject.asObservable();
  error$: Observable<string | null> = this.errorSubject.asObservable();
  result$: Observable<RecordingResult | null> = this.resultSubject.asObservable();

  get state(): RecordingState {
    return this.stateSubject.value;
  }

  get duration(): number {
    return this.durationSubject.value;
  }

  constructor(
    private http: HttpClient,
    private ngZone: NgZone
  ) {}

  /**
   * Start recording audio from the given MediaStream.
   * Uses MediaRecorder API with opus codec (stored as webm container).
   * Falls back to default mimeType if opus is not supported.
   */
  startRecording(stream: MediaStream): void {
    if (this.state === 'recording') {
      console.warn('Recording already in progress');
      return;
    }

    this.audioChunks = [];
    this.errorSubject.next(null);
    this.resultSubject.next(null);

    // Determine best supported mimeType
    const mimeTypes = [
      'audio/webm;codecs=opus',
      'audio/webm',
      'audio/ogg;codecs=opus',
      'audio/ogg',
      '',
    ];

    let selectedMimeType = '';
    for (const mimeType of mimeTypes) {
      if (!mimeType || MediaRecorder.isTypeSupported(mimeType)) {
        selectedMimeType = mimeType;
        break;
      }
    }

    try {
      const options: MediaRecorderOptions = {};
      if (selectedMimeType) {
        options.mimeType = selectedMimeType;
      }

      this.mediaRecorder = new MediaRecorder(stream, options);
      console.log(`MediaRecorder created with mimeType: ${this.mediaRecorder.mimeType}`);

      this.mediaRecorder.ondataavailable = (event: BlobEvent) => {
        if (event.data.size > 0) {
          this.audioChunks.push(event.data);
        }
      };

      this.mediaRecorder.onstart = () => {
        this.ngZone.run(() => {
          this.stateSubject.next('recording');
          this.recordingStartTime = Date.now();
          this.startDurationTimer();
        });
      };

      this.mediaRecorder.onstop = () => {
        this.ngZone.run(() => {
          this.stateSubject.next('stopping');
          this.stopDurationTimer();
          this.recordingDuration = (Date.now() - this.recordingStartTime) / 1000;
        });
      };

      this.mediaRecorder.onerror = (event: Event) => {
        const error = (event as ErrorEvent).error || 'Unknown MediaRecorder error';
        this.ngZone.run(() => {
          this.errorSubject.next(`Recording error: ${error.message || error}`);
          this.stateSubject.next('error');
        });
      };

      // Collect data every 1 second for better chunk management
      this.mediaRecorder.start(1000);
    } catch (err: any) {
      this.errorSubject.next(`Failed to start recording: ${err.message || err}`);
      this.stateSubject.next('error');
    }
  }

  /**
   * Stop recording and return the recorded blob.
   */
  stopRecording(): Blob | null {
    if (!this.mediaRecorder || this.state !== 'recording') {
      console.warn('No active recording to stop');
      return null;
    }

    // Stop the recorder - this triggers ondataavailable with remaining data
    this.mediaRecorder.stop();
    this.mediaRecorder = null;

    if (this.audioChunks.length === 0) {
      this.errorSubject.next('No audio data recorded');
      this.stateSubject.next('error');
      return null;
    }

    // Create blob from chunks
    const mimeType = this.audioChunks[0]?.type || 'audio/webm';
    const audioBlob = new Blob(this.audioChunks, { type: mimeType });
    this.audioChunks = [];

    return audioBlob;
  }

  /**
   * Upload recorded audio blob to the server.
   * POST /api/recordings/{roomId}/upload-audio
   */
  async uploadRecording(roomId: string, audioBlob: Blob): Promise<RecordingResult> {
    this.stateSubject.next('uploading');
    this.errorSubject.next(null);

    try {
      const formData = new FormData();
      const fileName = `meeting_${roomId.replace(/-/g, '')}.ogg`;

      // Determine file extension based on mime type
      let fileExt = 'webm';
      if (audioBlob.type.includes('ogg')) {
        fileExt = 'ogg';
      }

      const file = new File([audioBlob], `meeting_${roomId.replace(/-/g, '')}.${fileExt}`, {
        type: audioBlob.type,
      });
      formData.append('file', file);

      const result = await this.http.post<RecordingResult>(
        `${this.apiUrl}/api/recordings/${roomId}/upload-audio`,
        formData
      ).toPromise();

      if (result) {
        this.resultSubject.next(result);
        this.stateSubject.next('completed');
        console.log('Recording uploaded successfully:', result);
        return result;
      } else {
        throw new Error('Empty response from server');
      }
    } catch (err: any) {
      const errorMsg = `Upload failed: ${err.message || err}`;
      this.errorSubject.next(errorMsg);
      this.stateSubject.next('error');
      throw err;
    }
  }

  /**
   * Start and upload in one call - convenience method.
   */
  async recordAndUpload(roomId: string, stream: MediaStream): Promise<RecordingResult> {
    return new Promise((resolve, reject) => {
      this.startRecording(stream);

      // We need the caller to stop recording when ready
      // This method just sets up the recording, upload is separate
      reject(new Error('Use startRecording() then stopRecording() + uploadRecording() separately'));
    });
  }

  /**
   * Cancel the current recording without saving.
   */
  cancelRecording(): void {
    if (this.mediaRecorder && this.state === 'recording') {
      this.mediaRecorder.stop();
      this.mediaRecorder = null;
    }

    this.audioChunks = [];
    this.stopDurationTimer();
    this.stateSubject.next('idle');
    this.durationSubject.next(0);
  }

  /**
   * Reset state to idle.
   */
  reset(): void {
    this.cancelRecording();
    this.errorSubject.next(null);
    this.resultSubject.next(null);
  }

  private startDurationTimer(): void {
    this.durationInterval = window.setInterval(() => {
      this.ngZone.run(() => {
        this.durationSubject.next((Date.now() - this.recordingStartTime) / 1000);
      });
    }, 200);
  }

  private stopDurationTimer(): void {
    if (this.durationInterval !== null) {
      clearInterval(this.durationInterval);
      this.durationInterval = null;
    }
  }

  /**
   * Clean up on destroy.
   */
  destroy(): void {
    this.cancelRecording();
    this.stateSubject.complete();
    this.durationSubject.complete();
    this.errorSubject.complete();
    this.resultSubject.complete();
  }
}
