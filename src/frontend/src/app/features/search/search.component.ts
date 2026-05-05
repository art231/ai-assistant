import { Component } from '@angular/core';
import { DomSanitizer, SafeUrl } from '@angular/platform-browser';
import { ApiService, SearchResultDto } from '../../core/services/api.service';

@Component({
  selector: 'app-search',
  template: `
    <div class="search-container">
      <div class="card">
        <h2>Search Meeting Transcripts</h2>
        <div class="search-bar">
          <input
            type="text"
            [(ngModel)]="query"
            placeholder="Search for keywords..."
            class="search-input"
            (keyup.enter)="search()"
          />
          <button class="btn btn-primary" (click)="search()" [disabled]="!query">
            Search
          </button>
        </div>
      </div>

      <div class="results-container" *ngIf="results.length > 0">
        <h3>Results ({{ results.length }})</h3>
        <div *ngFor="let result of results" class="result-item card">
          <div class="result-header">
            <span class="result-room">{{ result.roomName }}</span>
            <span class="result-time">{{ result.startedAt | date:'medium' }}</span>
          </div>
          <p class="result-snippet">{{ result.transcriptSnippet }}</p>
          <div class="result-actions">
            <button class="btn btn-secondary btn-sm" (click)="playRecording(result.recordingId)">
              {{ currentAudioRecordingId === result.recordingId && isPlaying ? 'Stop' : 'Play' }} Recording
            </button>
            <button class="btn btn-secondary btn-sm" (click)="downloadPdf(result.recordingId)">
              Download PDF
            </button>
          </div>
          <div *ngIf="currentAudioRecordingId === result.recordingId && currentAudioUrl" class="audio-player">
            <audio controls autoplay [src]="currentAudioUrl" (ended)="isPlaying = false">
              Your browser does not support the audio element.
            </audio>
          </div>
        </div>
      </div>

      <div class="empty-state" *ngIf="searched && results.length === 0">
        <p>No results found for "{{ query }}"</p>
      </div>
    </div>
  `,
  styles: [
    `
    .search-container {
      max-width: 800px;
      margin: 0 auto;
    }

    .search-bar {
      display: flex;
      gap: 12px;
      margin-top: 16px;
    }

    .search-input {
      flex: 1;
    }

    .results-container {
      margin-top: 24px;
    }

    .result-item {
      cursor: pointer;
      transition: transform 0.2s;
    }

    .result-item:hover {
      transform: translateX(4px);
    }

    .result-header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      margin-bottom: 8px;
    }

    .result-room {
      font-weight: 600;
      color: #533483;
    }

    .result-time {
      font-size: 12px;
      color: #666;
    }

    .result-snippet {
      color: #aaa;
      line-height: 1.5;
      margin-bottom: 12px;
    }

    .result-actions {
      display: flex;
      gap: 8px;
    }

    .btn-sm {
      padding: 6px 12px;
      font-size: 12px;
    }

    .audio-player {
      margin-top: 8px;
    }

    .audio-player audio {
      width: 100%;
      max-width: 400px;
      height: 36px;
    }

    .empty-state {
      text-align: center;
      padding: 40px;
      color: #666;
    }
    `,
  ],
})
export class SearchComponent {
  query = '';
  results: SearchResultDto[] = [];
  searched = false;
  currentAudioUrl: SafeUrl | null = null;
  currentAudioRecordingId: string | null = null;
  isPlaying = false;

  constructor(
    private apiService: ApiService,
    private sanitizer: DomSanitizer
  ) {}

  search(): void {
    if (!this.query) return;

    this.searched = true;
    this.apiService.searchTranscripts(this.query).subscribe({
      next: (results) => (this.results = results),
      error: (err) => console.error('Search failed:', err),
    });
  }

  downloadPdf(recordingId: string): void {
    const url = `${this.apiService['apiUrl']}/api/recordings/${recordingId}/export-pdf`;
    window.open(url, '_blank');
  }

  playRecording(recordingId: string): void {
    if (this.currentAudioRecordingId === recordingId && this.isPlaying) {
      // Toggle off
      this.currentAudioUrl = null;
      this.currentAudioRecordingId = null;
      this.isPlaying = false;
      return;
    }

    const url = `${this.apiService['apiUrl']}/api/recordings/${recordingId}/audio`;
    this.currentAudioUrl = this.sanitizer.bypassSecurityTrustUrl(url);
    this.currentAudioRecordingId = recordingId;
    this.isPlaying = true;
  }
}
