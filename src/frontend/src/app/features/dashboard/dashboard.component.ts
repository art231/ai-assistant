import { Component, OnInit, AfterViewInit, ViewChild, ElementRef, OnDestroy } from '@angular/core';
import { Chart, registerables } from 'chart.js';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../../core/services/api.service';

Chart.register(...registerables);

interface MeetingEfficiency {
  roomId: string;
  roomName: string;
  date: string;
  durationMinutes: number;
  participantCount: number;
  topicChanges: number;
  adviceCount: number;
  efficiencyScore: number;
}

@Component({
  selector: 'app-dashboard',
  template: `
    <div class="dashboard-container">
      <h2>Meeting Efficiency Dashboard</h2>

      <div class="summary-cards">
        <div class="summary-card">
          <div class="summary-value">{{ totalMeetings }}</div>
          <div class="summary-label">Total Meetings</div>
        </div>
        <div class="summary-card">
          <div class="summary-value">{{ avgEfficiency }}%</div>
          <div class="summary-label">Avg Efficiency</div>
        </div>
        <div class="summary-card">
          <div class="summary-value">{{ totalTopicChanges }}</div>
          <div class="summary-label">Topic Changes</div>
        </div>
        <div class="summary-card">
          <div class="summary-value">{{ totalAdvice }}</div>
          <div class="summary-label">Advice Generated</div>
        </div>
      </div>

      <div class="charts-grid">
        <div class="card chart-card">
          <h3>Efficiency Score Trend</h3>
          <div class="chart-wrapper">
            <canvas #efficiencyChart></canvas>
          </div>
        </div>

        <div class="card chart-card">
          <h3>Duration vs Topic Changes</h3>
          <div class="chart-wrapper">
            <canvas #durationChart></canvas>
          </div>
        </div>

        <div class="card chart-card">
          <h3>Advice per Meeting</h3>
          <div class="chart-wrapper">
            <canvas #adviceChart></canvas>
          </div>
        </div>

        <div class="card chart-card">
          <h3>Participant Distribution</h3>
          <div class="chart-wrapper">
            <canvas #participantChart></canvas>
          </div>
        </div>
      </div>

      <div class="card meetings-table-section">
        <h3>Meeting Details</h3>
        <div class="table-wrapper">
          <table class="meetings-table">
            <thead>
              <tr>
                <th>Date</th>
                <th>Room</th>
                <th>Duration</th>
                <th>Participants</th>
                <th>Topics</th>
                <th>Advice</th>
                <th>Score</th>
                <th>PDF</th>
              </tr>
            </thead>
            <tbody>
              <tr *ngFor="let m of meetings">
                <td>{{ m.date | date:'short' }}</td>
                <td>{{ m.roomName }}</td>
                <td>{{ m.durationMinutes }}m</td>
                <td>{{ m.participantCount }}</td>
                <td>{{ m.topicChanges }}</td>
                <td>{{ m.adviceCount }}</td>
                <td>
                  <span class="score-badge" [class.high]="m.efficiencyScore >= 70"
                    [class.medium]="m.efficiencyScore >= 40 && m.efficiencyScore < 70"
                    [class.low]="m.efficiencyScore < 40">
                    {{ m.efficiencyScore }}%
                  </span>
                </td>
                <td>
                  <button class="btn btn-sm btn-pdf" (click)="downloadPdf(m.roomId)" title="Скачать PDF-отчёт">
                    📄
                  </button>
                </td>
              </tr>
              <tr *ngIf="meetings.length === 0">
                <td colspan="8" class="empty-state">No meeting data available.</td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>
    </div>
  `,
  styles: [
    `
    .dashboard-container {
      max-width: 1200px;
      margin: 0 auto;
    }

    .summary-cards {
      display: grid;
      grid-template-columns: repeat(4, 1fr);
      gap: 16px;
      margin-bottom: 24px;
    }

    .summary-card {
      background-color: #1a1a2e;
      border-radius: 12px;
      padding: 20px;
      text-align: center;
    }

    .summary-value {
      font-size: 28px;
      font-weight: 700;
      color: #533483;
    }

    .summary-label {
      font-size: 13px;
      color: #888;
      margin-top: 4px;
    }

    .charts-grid {
      display: grid;
      grid-template-columns: repeat(2, 1fr);
      gap: 16px;
      margin-bottom: 24px;
    }

    .chart-card {
      min-height: 300px;
    }

    .chart-wrapper {
      height: 250px;
      margin-top: 12px;
      position: relative;
    }

    .chart-wrapper canvas {
      max-height: 250px;
    }

    .meetings-table-section {
      margin-top: 24px;
    }

    .table-wrapper {
      overflow-x: auto;
      margin-top: 12px;
    }

    .meetings-table {
      width: 100%;
      border-collapse: collapse;
    }

    .meetings-table th,
    .meetings-table td {
      padding: 10px 12px;
      text-align: left;
      border-bottom: 1px solid #1a1a2e;
    }

    .meetings-table th {
      color: #888;
      font-size: 12px;
      text-transform: uppercase;
      letter-spacing: 0.5px;
    }

    .meetings-table td {
      color: #ccc;
      font-size: 13px;
    }

    .score-badge {
      padding: 3px 8px;
      border-radius: 4px;
      font-size: 12px;
      font-weight: 600;
    }

    .score-badge.high {
      background-color: #1a8a3f;
      color: #fff;
    }

    .score-badge.medium {
      background-color: #b8860b;
      color: #fff;
    }

    .score-badge.low {
      background-color: #e94560;
      color: #fff;
    }

    .empty-state {
      text-align: center;
      color: #666;
      padding: 20px;
    }

    .btn-pdf {
      background: none;
      border: 1px solid #533483;
      border-radius: 4px;
      cursor: pointer;
      font-size: 16px;
      padding: 2px 6px;
      transition: background-color 0.2s;
    }

    .btn-pdf:hover {
      background-color: #533483;
    }
    `,
  ],
})
export class DashboardComponent implements OnInit, AfterViewInit, OnDestroy {
  @ViewChild('efficiencyChart') efficiencyCanvas!: ElementRef<HTMLCanvasElement>;
  @ViewChild('durationChart') durationCanvas!: ElementRef<HTMLCanvasElement>;
  @ViewChild('adviceChart') adviceCanvas!: ElementRef<HTMLCanvasElement>;
  @ViewChild('participantChart') participantCanvas!: ElementRef<HTMLCanvasElement>;

  meetings: MeetingEfficiency[] = [];
  totalMeetings = 0;
  avgEfficiency = 0;
  totalTopicChanges = 0;
  totalAdvice = 0;

  private charts: Chart[] = [];

  constructor(private apiService: ApiService) {}

  ngOnInit(): void {
    this.loadData();
  }

  ngAfterViewInit(): void {
    // Charts will be created after data loads
  }

  ngOnDestroy(): void {
    this.charts.forEach((c) => c.destroy());
  }

  loadData(): void {
    this.apiService.getMeetingEfficiency().subscribe({
      next: (data) => {
        this.meetings = data;
        this.calculateSummaries();
        setTimeout(() => this.createCharts(), 100);
      },
      error: (err) => console.error('Failed to load efficiency data:', err),
    });
  }

  private calculateSummaries(): void {
    this.totalMeetings = this.meetings.length;
    this.totalTopicChanges = this.meetings.reduce(
      (sum, m) => sum + m.topicChanges,
      0
    );
    this.totalAdvice = this.meetings.reduce((sum, m) => sum + m.adviceCount, 0);
    this.avgEfficiency =
      this.meetings.length > 0
        ? Math.round(
            this.meetings.reduce((sum, m) => sum + m.efficiencyScore, 0) /
              this.meetings.length
          )
        : 0;
  }

  /**
   * Download PDF report for a specific room.
   */
  async downloadPdf(roomId: string): Promise<void> {
    try {
      const blob = await firstValueFrom(this.apiService.exportRoomPdf(roomId));
      if (!blob) return;

      const url = window.URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `отчёт_встречи_${new Date().toISOString().split('T')[0]}.pdf`;
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
      window.URL.revokeObjectURL(url);
    } catch (err: any) {
      console.error('Failed to download PDF:', err);
    }
  }

  private createCharts(): void {
    if (!this.efficiencyCanvas) return;

    const sorted = [...this.meetings].sort(
      (a, b) => new Date(a.date).getTime() - new Date(b.date).getTime()
    );
    const labels = sorted.map((m) =>
      new Date(m.date).toLocaleDateString('en-US', {
        month: 'short',
        day: 'numeric',
      })
    );

    // 1. Efficiency Score Trend (Line chart)
    this.charts.push(
      new Chart(this.efficiencyCanvas.nativeElement, {
        type: 'line',
        data: {
          labels,
          datasets: [
            {
              label: 'Efficiency Score',
              data: sorted.map((m) => m.efficiencyScore),
              borderColor: '#533483',
              backgroundColor: 'rgba(83, 52, 131, 0.1)',
              fill: true,
              tension: 0.4,
            },
          ],
        },
        options: {
          responsive: true,
          maintainAspectRatio: false,
          plugins: {
            legend: { display: false },
          },
          scales: {
            y: {
              min: 0,
              max: 100,
              ticks: { color: '#888' },
              grid: { color: 'rgba(255,255,255,0.05)' },
            },
            x: {
              ticks: { color: '#888' },
              grid: { display: false },
            },
          },
        },
      })
    );

    // 2. Duration vs Topic Changes (Bar chart)
    this.charts.push(
      new Chart(this.durationCanvas.nativeElement, {
        type: 'bar',
        data: {
          labels,
          datasets: [
            {
              label: 'Duration (min)',
              data: sorted.map((m) => m.durationMinutes),
              backgroundColor: '#0f3460',
            },
            {
              label: 'Topic Changes',
              data: sorted.map((m) => m.topicChanges),
              backgroundColor: '#e94560',
            },
          ],
        },
        options: {
          responsive: true,
          maintainAspectRatio: false,
          plugins: {
            legend: {
              labels: { color: '#888', font: { size: 11 } },
            },
          },
          scales: {
            y: {
              beginAtZero: true,
              ticks: { color: '#888' },
              grid: { color: 'rgba(255,255,255,0.05)' },
            },
            x: {
              ticks: { color: '#888' },
              grid: { display: false },
            },
          },
        },
      })
    );

    // 3. Advice per Meeting (Doughnut chart)
    this.charts.push(
      new Chart(this.adviceCanvas.nativeElement, {
        type: 'doughnut',
        data: {
          labels: sorted.map((m) => m.roomName),
          datasets: [
            {
              data: sorted.map((m) => m.adviceCount),
              backgroundColor: [
                '#533483',
                '#0f3460',
                '#e94560',
                '#1a8a3f',
                '#b8860b',
                '#ff6b6b',
                '#4ecdc4',
                '#45b7d1',
              ],
            },
          ],
        },
        options: {
          responsive: true,
          maintainAspectRatio: false,
          plugins: {
            legend: {
              position: 'right',
              labels: { color: '#888', font: { size: 10 } },
            },
          },
        },
      })
    );

    // 4. Participant Distribution (Polar area chart)
    this.charts.push(
      new Chart(this.participantCanvas.nativeElement, {
        type: 'polarArea',
        data: {
          labels: sorted.map((m) => m.roomName),
          datasets: [
            {
              data: sorted.map((m) => m.participantCount),
              backgroundColor: [
                'rgba(83, 52, 131, 0.7)',
                'rgba(15, 52, 96, 0.7)',
                'rgba(233, 69, 96, 0.7)',
                'rgba(26, 138, 63, 0.7)',
                'rgba(184, 134, 11, 0.7)',
                'rgba(255, 107, 107, 0.7)',
                'rgba(78, 205, 196, 0.7)',
                'rgba(69, 183, 209, 0.7)',
              ],
            },
          ],
        },
        options: {
          responsive: true,
          maintainAspectRatio: false,
          plugins: {
            legend: {
              position: 'right',
              labels: { color: '#888', font: { size: 10 } },
            },
          },
          scales: {
            r: {
              ticks: { color: '#888', backdropColor: 'transparent' },
              grid: { color: 'rgba(255,255,255,0.05)' },
            },
          },
        },
      })
    );
  }
}
