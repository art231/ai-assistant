import { Component, OnInit } from '@angular/core';
import { ApiService, MeetingRecordingDto } from '../../core/services/api.service';

@Component({
  selector: 'app-analytics',
  template: `
    <div class="analytics-container">
      <h2>My Analytics</h2>

      <div class="stats-grid">
        <div class="stat-card">
          <div class="stat-value">{{ totalMeetings }}</div>
          <div class="stat-label">Total Meetings</div>
        </div>
        <div class="stat-card">
          <div class="stat-value">{{ totalWords }}</div>
          <div class="stat-label">Words Spoken</div>
        </div>
        <div class="stat-card">
          <div class="stat-value">{{ totalRecordings }}</div>
          <div class="stat-label">Recordings</div>
        </div>
        <div class="stat-card">
          <div class="stat-value">{{ avgDuration }}m</div>
          <div class="stat-label">Avg Duration</div>
        </div>
      </div>

      <div class="card recordings-section">
        <h3>Recent Recordings</h3>
        <div class="recordings-list">
          <div *ngFor="let rec of recordings" class="recording-item">
            <div class="recording-info">
              <span class="recording-date">{{ rec.startedAt | date:'medium' }}</span>
              <span class="recording-duration">{{ rec.durationSeconds }}s</span>
              <span class="recording-status" [class.completed]="rec.status === 'Completed'">
                {{ rec.status }}
              </span>
            </div>
            <button
              class="btn btn-secondary btn-sm"
              (click)="downloadPdf(rec.id)"
              [disabled]="rec.status !== 'Completed'"
            >
              Download PDF
            </button>
          </div>
          <div *ngIf="recordings.length === 0" class="empty-state">
            No recordings yet.
          </div>
        </div>
      </div>
    </div>
  `,
  styles: [
    `
    .analytics-container {
      max-width: 900px;
      margin: 0 auto;
    }

    .stats-grid {
      display: grid;
      grid-template-columns: repeat(4, 1fr);
      gap: 16px;
      margin-bottom: 24px;
    }

    .stat-card {
      background-color: #1a1a2e;
      border-radius: 12px;
      padding: 20px;
      text-align: center;
    }

    .stat-value {
      font-size: 32px;
      font-weight: 700;
      color: #e94560;
    }

    .stat-label {
      font-size: 13px;
      color: #888;
      margin-top: 4px;
    }

    .recordings-section {
      margin-top: 24px;
    }

    .recordings-list {
      margin-top: 12px;
    }

    .recording-item {
      display: flex;
      justify-content: space-between;
      align-items: center;
      padding: 12px;
      background-color: #1a1a2e;
      border-radius: 8px;
      margin-bottom: 8px;
    }

    .recording-info {
      display: flex;
      gap: 16px;
      align-items: center;
    }

    .recording-date {
      color: #ccc;
      font-size: 13px;
    }

    .recording-duration {
      color: #888;
      font-size: 12px;
    }

    .recording-status {
      padding: 2px 8px;
      border-radius: 4px;
      font-size: 11px;
      background-color: #333;
    }

    .recording-status.completed {
      background-color: #1a8a3f;
    }

    .btn-sm {
      padding: 6px 12px;
      font-size: 12px;
    }

    .empty-state {
      color: #666;
      text-align: center;
      padding: 20px;
    }
    `,
  ],
})
export class AnalyticsComponent implements OnInit {
  totalMeetings = 0;
  totalWords = 0;
  totalRecordings = 0;
  avgDuration = 0;
  recordings: MeetingRecordingDto[] = [];

  constructor(private apiService: ApiService) {}

  ngOnInit(): void {
    this.loadRecordings();
  }

  loadRecordings(): void {
    this.apiService.getRecordings().subscribe({
      next: (recordings) => {
        this.recordings = recordings;
        this.totalRecordings = recordings.length;
        this.totalMeetings = new Set(recordings.map((r) => r.roomId)).size;
        this.totalWords = recordings.reduce(
          (sum, r) => sum + (r.transcript?.split(' ').length ?? 0),
          0
        );
        this.avgDuration =
          recordings.length > 0
            ? Math.round(
                recordings.reduce((sum, r) => sum + r.duration, 0) /
                  recordings.length /
                  60
              )
            : 0;
      },
      error: (err) => console.error('Failed to load recordings:', err),
    });
  }

  downloadPdf(recordingId: string): void {
    const url = `${this.apiService['apiUrl']}/api/recordings/${recordingId}/export-pdf`;
    window.open(url, '_blank');
  }
}
