import { Injectable, NgZone } from '@angular/core';
import { BehaviorSubject, Observable } from 'rxjs';

export interface MicrophoneDevice {
  deviceId: string;
  label: string;
}

export type MicrophoneState = 'unavailable' | 'permission-required' | 'ready' | 'active' | 'error';

@Injectable({
  providedIn: 'root',
})
export class MicrophoneService {
  private stream: MediaStream | null = null;
  private audioContext: AudioContext | null = null;
  private analyserNode: AnalyserNode | null = null;
  private animationFrameId: number | null = null;

  private stateSubject = new BehaviorSubject<MicrophoneState>('unavailable');
  private devicesSubject = new BehaviorSubject<MicrophoneDevice[]>([]);
  private audioLevelSubject = new BehaviorSubject<number>(0);
  private errorSubject = new BehaviorSubject<string | null>(null);
  private isMutedSubject = new BehaviorSubject<boolean>(false);

  state$: Observable<MicrophoneState> = this.stateSubject.asObservable();
  devices$: Observable<MicrophoneDevice[]> = this.devicesSubject.asObservable();
  audioLevel$: Observable<number> = this.audioLevelSubject.asObservable();
  error$: Observable<string | null> = this.errorSubject.asObservable();
  isMuted$: Observable<boolean> = this.isMutedSubject.asObservable();

  get state(): MicrophoneState {
    return this.stateSubject.value;
  }

  get streamAvailable(): MediaStream | null {
    return this.stream;
  }

  get isMuted(): boolean {
    return this.isMutedSubject.value;
  }

  constructor(private ngZone: NgZone) {
    this.checkAvailability();
  }

  /**
   * Check if microphone API is available in the browser.
   */
  private checkAvailability(): void {
    if (typeof navigator === 'undefined' || !navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
      this.stateSubject.next('unavailable');
      this.errorSubject.next('Microphone API is not available in this browser');
      return;
    }
    this.stateSubject.next('permission-required');
    this.enumerateDevices();
  }

  /**
   * Enumerate available audio input devices.
   */
  async enumerateDevices(): Promise<MicrophoneDevice[]> {
    try {
      const devices = await navigator.mediaDevices.enumerateDevices();
      const audioInputs = devices
        .filter(d => d.kind === 'audioinput')
        .map(d => ({
          deviceId: d.deviceId,
          label: d.label || `Microphone ${d.deviceId.slice(0, 8)}...`,
        }));

      this.devicesSubject.next(audioInputs);

      // Listen for device changes
      navigator.mediaDevices.addEventListener('devicechange', () => {
        this.enumerateDevices();
      });

      return audioInputs;
    } catch (err: any) {
      console.error('Failed to enumerate devices:', err);
      return [];
    }
  }

  /**
   * Request microphone access and start the audio stream.
   */
  async requestAccess(deviceId?: string): Promise<MediaStream> {
    // Stop any existing stream first
    this.stopStream();

    const constraints: MediaStreamConstraints = {
      audio: deviceId
        ? { deviceId: { exact: deviceId } }
        : true,
    };

    try {
      this.errorSubject.next(null);
      const stream = await navigator.mediaDevices.getUserMedia(constraints);
      this.stream = stream;
      this.stateSubject.next('active');
      this.isMutedSubject.next(false);

      // Set up audio level analysis
      this.setupAudioAnalysis(stream);

      // Update device labels now that we have permission
      await this.enumerateDevices();

      return stream;
    } catch (err: any) {
      let errorMsg: string;
      switch (err.name) {
        case 'NotAllowedError':
        case 'PermissionDeniedError':
          errorMsg = 'Microphone access denied. Please allow microphone access in your browser settings.';
          this.stateSubject.next('permission-required');
          break;
        case 'NotFoundError':
          errorMsg = 'No microphone found. Please connect a microphone.';
          this.stateSubject.next('unavailable');
          break;
        case 'NotReadableError':
          errorMsg = 'Microphone is busy. Please close other apps using the microphone.';
          this.stateSubject.next('error');
          break;
        default:
          errorMsg = `Microphone error: ${err.message || err.name || 'Unknown error'}`;
          this.stateSubject.next('error');
      }

      this.errorSubject.next(errorMsg);
      console.error('Microphone access error:', err);
      throw err;
    }
  }

  /**
   * Set up audio analysis for level metering.
   */
  private setupAudioAnalysis(stream: MediaStream): void {
    try {
      this.audioContext = new AudioContext();
      const source = this.audioContext.createMediaStreamSource(stream);
      this.analyserNode = this.audioContext.createAnalyser();
      this.analyserNode.fftSize = 256;
      source.connect(this.analyserNode);
      this.startAudioLevelMetering();
    } catch (err) {
      console.error('Failed to set up audio analysis:', err);
    }
  }

  /**
   * Start metering audio levels for visualization.
   */
  private startAudioLevelMetering(): void {
    if (!this.analyserNode) return;

    const dataArray = new Uint8Array(this.analyserNode.frequencyBinCount);

    const updateLevel = () => {
      if (!this.analyserNode) return;

      this.analyserNode.getByteFrequencyData(dataArray);

      // Calculate average audio level (0-1)
      let sum = 0;
      for (let i = 0; i < dataArray.length; i++) {
        sum += dataArray[i];
      }
      const average = sum / dataArray.length / 255;

      this.ngZone.run(() => {
        this.audioLevelSubject.next(average);
      });

      this.animationFrameId = requestAnimationFrame(updateLevel);
    };

    updateLevel();
  }

  /**
   * Toggle mute/unmute for the microphone.
   */
  toggleMute(): boolean {
    if (!this.stream) return false;

    const audioTracks = this.stream.getAudioTracks();
    if (audioTracks.length === 0) return false;

    const newMuted = !this.isMutedSubject.value;
    audioTracks.forEach(track => (track.enabled = !newMuted));
    this.isMutedSubject.next(newMuted);

    if (newMuted) {
      this.audioLevelSubject.next(0);
    }

    return newMuted;
  }

  /**
   * Set mute state explicitly.
   */
  setMuted(muted: boolean): void {
    if (!this.stream) return;

    const audioTracks = this.stream.getAudioTracks();
    audioTracks.forEach(track => (track.enabled = !muted));
    this.isMutedSubject.next(muted);

    if (muted) {
      this.audioLevelSubject.next(0);
    }
  }

  /**
   * Get the current audio stream (for WebRTC).
   */
  getStream(): MediaStream | null {
    return this.stream;
  }

  /**
   * Stop the microphone stream and clean up.
   */
  stopStream(): void {
    if (this.animationFrameId !== null) {
      cancelAnimationFrame(this.animationFrameId);
      this.animationFrameId = null;
    }

    if (this.audioContext) {
      this.audioContext.close().catch(() => {});
      this.audioContext = null;
    }

    if (this.stream) {
      this.stream.getTracks().forEach(track => track.stop());
      this.stream = null;
    }

    this.analyserNode = null;
    this.audioLevelSubject.next(0);
    this.isMutedSubject.next(false);
    this.stateSubject.next('permission-required');
  }

  /**
   * Clean up on destroy.
   */
  destroy(): void {
    this.stopStream();
    this.stateSubject.complete();
    this.devicesSubject.complete();
    this.audioLevelSubject.complete();
    this.errorSubject.complete();
    this.isMutedSubject.complete();
  }
}
