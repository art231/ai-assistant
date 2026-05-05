import { Component, OnInit, OnDestroy } from '@angular/core';
import { SignalrService } from './core/services/signalr.service';

@Component({
  selector: 'app-root',
  template: `
    <div class="app-container">
      <header class="app-header">
        <div class="header-content">
          <h1 class="app-title">VoiceChatAI</h1>
          <nav class="app-nav">
            <a routerLink="/meeting" class="nav-link">Meeting</a>
            <a routerLink="/search" class="nav-link">Search</a>
            <a routerLink="/analytics" class="nav-link">Analytics</a>
            <a routerLink="/dashboard" class="nav-link">Dashboard</a>
          </nav>
          <div class="connection-status">
            <span
              class="status-indicator"
              [class.connected]="isConnected"
              [class.disconnected]="!isConnected"
            ></span>
            <span class="status-text">
              {{ isConnected ? 'Connected' : 'Disconnected' }}
            </span>
          </div>
        </div>
      </header>
      <main class="app-main">
        <router-outlet></router-outlet>
      </main>
    </div>
  `,
  styles: [
    `
    .app-container {
      min-height: 100vh;
      display: flex;
      flex-direction: column;
    }

    .app-header {
      background-color: #0f3460;
      padding: 12px 24px;
      box-shadow: 0 2px 8px rgba(0, 0, 0, 0.3);
    }

    .header-content {
      max-width: 1200px;
      margin: 0 auto;
      display: flex;
      align-items: center;
      justify-content: space-between;
    }

    .app-title {
      font-size: 24px;
      font-weight: 700;
      color: #e94560;
    }

    .app-nav {
      display: flex;
      gap: 16px;
    }

    .nav-link {
      color: #e0e0e0;
      text-decoration: none;
      padding: 8px 16px;
      border-radius: 6px;
      transition: background-color 0.2s;
    }

    .nav-link:hover {
      background-color: rgba(255, 255, 255, 0.1);
    }

    .connection-status {
      display: flex;
      align-items: center;
      gap: 8px;
    }

    .status-indicator {
      width: 10px;
      height: 10px;
      border-radius: 50%;
    }

    .status-indicator.connected {
      background-color: #4caf50;
      box-shadow: 0 0 6px #4caf50;
    }

    .status-indicator.disconnected {
      background-color: #f44336;
    }

    .status-text {
      font-size: 12px;
      color: #aaa;
    }

    .app-main {
      flex: 1;
      padding: 24px;
    }
    `,
  ],
})
export class AppComponent implements OnInit, OnDestroy {
  isConnected = false;

  constructor(private signalrService: SignalrService) {}

  ngOnInit(): void {
    this.signalrService.connected$.subscribe(
      (connected) => (this.isConnected = connected)
    );
    this.signalrService.startConnection();
  }

  ngOnDestroy(): void {
    this.signalrService.stopConnection();
  }
}
