import { Component, OnInit, OnDestroy, effect, computed, untracked } from '@angular/core';
import { AuthService } from '../auth/auth.service';
import {
  FetchStatusValue,
  WorkoutFetchService,
  startOfLocalDayUtcIso,
  startOfNextLocalDayUtcIso,
} from '../workouts/workout-fetch.service';
import { AnalysisService } from '../workouts/analysis.service';
import { FitnessTrendChartComponent } from './fitness-trend-chart.component';

const ANALYSIS_RETRIGGER_DEBOUNCE_MS = 300;

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [FitnessTrendChartComponent],
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.scss',
})
export class DashboardComponent implements OnInit, OnDestroy {
  private previousFetchStatus: FetchStatusValue | null = null;
  private analysisRetriggerTimeoutId: ReturnType<typeof setTimeout> | null = null;

  constructor(
    public authService: AuthService,
    public fetchService: WorkoutFetchService,
    public analysisService: AnalysisService,
  ) {
    effect(() => {
      const status = this.fetchService.status().status;
      if (status === 'completed' && this.previousFetchStatus !== 'completed') {
        const [from, to] = this.pickerWindowIso();
        this.analysisService.load(from, to);
      }
      this.previousFetchStatus = status;
    });

    effect(() => {
      const from = this.fetchService.fromDate();
      const to = this.fetchService.toDate();
      // Read status without tracking so this effect only re-runs on picker changes,
      // not on every status flip (which would double-fire alongside the completion effect).
      const status = untracked(() => this.fetchService.status().status);
      if (status !== 'completed') return;

      if (this.analysisRetriggerTimeoutId !== null) {
        clearTimeout(this.analysisRetriggerTimeoutId);
      }
      this.analysisRetriggerTimeoutId = setTimeout(() => {
        this.analysisRetriggerTimeoutId = null;
        this.analysisService.load(
          from ? startOfLocalDayUtcIso(from) : undefined,
          to ? startOfNextLocalDayUtcIso(to) : undefined,
        );
      }, ANALYSIS_RETRIGGER_DEBOUNCE_MS);
    });
  }

  private pickerWindowIso(): [string | undefined, string | undefined] {
    const from = this.fetchService.fromDate();
    const to = this.fetchService.toDate();
    return [
      from ? startOfLocalDayUtcIso(from) : undefined,
      to ? startOfNextLocalDayUtcIso(to) : undefined,
    ];
  }

  invalidRange = computed(() => {
    const from = this.fetchService.fromDate();
    const to = this.fetchService.toDate();
    return !!from && !!to && from > to;
  });

  ngOnInit() {
    this.fetchService.checkStatus();
  }

  ngOnDestroy() {
    if (this.analysisRetriggerTimeoutId !== null) {
      clearTimeout(this.analysisRetriggerTimeoutId);
      this.analysisRetriggerTimeoutId = null;
    }
  }

  trigger() {
    this.fetchService.trigger();
  }

  onFromChange(event: Event) {
    const value = (event.target as HTMLInputElement).value;
    this.fetchService.fromDate.set(value || null);
  }

  onToChange(event: Event) {
    const value = (event.target as HTMLInputElement).value;
    this.fetchService.toDate.set(value || null);
  }
}
