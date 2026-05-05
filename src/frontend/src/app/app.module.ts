import { NgModule } from '@angular/core';
import { BrowserModule } from '@angular/platform-browser';
import { HttpClientModule } from '@angular/common/http';
import { FormsModule } from '@angular/forms';
import { RouterModule, Routes } from '@angular/router';

import { AppComponent } from './app.component';
import { MeetingComponent } from './features/meeting/meeting.component';
import { SearchComponent } from './features/search/search.component';
import { AnalyticsComponent } from './features/analytics/analytics.component';
import { DashboardComponent } from './features/dashboard/dashboard.component';

const routes: Routes = [
  { path: '', redirectTo: '/meeting', pathMatch: 'full' },
  { path: 'meeting', component: MeetingComponent },
  { path: 'meeting/:roomId', component: MeetingComponent },
  { path: 'search', component: SearchComponent },
  { path: 'analytics', component: AnalyticsComponent },
  { path: 'dashboard', component: DashboardComponent },
];

@NgModule({
  declarations: [
    AppComponent,
    MeetingComponent,
    SearchComponent,
    AnalyticsComponent,
    DashboardComponent,
  ],
  imports: [
    BrowserModule,
    HttpClientModule,
    FormsModule,
    RouterModule.forRoot(routes),
  ],
  providers: [],
  bootstrap: [AppComponent],
})
export class AppModule {}
