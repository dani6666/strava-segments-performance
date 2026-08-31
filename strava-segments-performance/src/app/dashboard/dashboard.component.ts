import { Component, OnInit, effect } from '@angular/core';
import { AuthService } from '../auth/auth.service';
import { FetchStatusValue, WorkoutFetchService } from '../workouts/workout-fetch.service';
import { AnalysisService } from '../workouts/analysis.service';
import { FitnessTrendChartComponent } from './fitness-trend-chart.component';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [FitnessTrendChartComponent],
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.scss'
})
export class DashboardComponent implements OnInit {
  private previousFetchStatus: FetchStatusValue | null = null;

  constructor(
    public authService: AuthService,
    public fetchService: WorkoutFetchService,
    public analysisService: AnalysisService
  ) {
    effect(() => {
      const status = this.fetchService.status().status;
      if (status === 'completed' && this.previousFetchStatus !== 'completed') {
        this.analysisService.load();
      }
      this.previousFetchStatus = status;
    });
  }

  ngOnInit() {
    this.fetchService.checkStatus();
  }

  trigger() {
    this.fetchService.trigger();
  }
}
