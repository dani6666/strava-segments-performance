import { Injectable, signal } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { EMPTY, Subject, catchError, switchMap } from 'rxjs';
import { environment } from '../../environments/environment';

export interface FitnessTrendPoint {
  date: string;
  score: number;
}

export type AnalysisLoadState = 'idle' | 'loading' | 'loaded' | 'error';

interface LoadRequest {
  from?: string;
  to?: string;
}

@Injectable({ providedIn: 'root' })
export class AnalysisService {
  series = signal<FitnessTrendPoint[] | null>(null);
  loadState = signal<AnalysisLoadState>('idle');

  // switchMap here cancels any in-flight request when a newer one arrives,
  // so out-of-order responses can't overwrite series with stale data.
  private readonly requests$ = new Subject<LoadRequest>();

  constructor(private http: HttpClient) {
    this.requests$
      .pipe(
        switchMap(({ from, to }) => {
          let params = new HttpParams();
          if (from) params = params.set('from', from);
          if (to) params = params.set('to', to);
          return this.http
            .get<
              FitnessTrendPoint[]
            >(`${environment.apiBaseUrl}/api/analysis/fitness-trend`, { params, withCredentials: true })
            .pipe(
              // Swallow per-request errors inside switchMap so a single failure
              // doesn't tear down the outer subscription and stop future loads.
              catchError(() => {
                this.setFailed();
                return EMPTY;
              }),
            );
        }),
      )
      .subscribe((series) => {
        this.series.set(series);
        this.loadState.set('loaded');
      });
  }

  load(from?: string, to?: string) {
    this.loadState.set('loading');
    this.requests$.next({ from, to });
  }

  reset() {
    this.series.set(null);
    this.loadState.set('idle');
  }

  private setFailed() {
    this.loadState.set('error');
  }
}
