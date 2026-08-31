import { Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { tap } from 'rxjs';
import { environment } from '../../environments/environment';

export interface FitnessTrendPoint {
  date: string;
  score: number;
}

export type AnalysisLoadState = 'idle' | 'loading' | 'loaded' | 'error';

@Injectable({ providedIn: 'root' })
export class AnalysisService {
  series = signal<FitnessTrendPoint[] | null>(null);
  loadState = signal<AnalysisLoadState>('idle');

  constructor(private http: HttpClient) {}

  load() {
    this.loadState.set('loading');
    return this.http
      .get<FitnessTrendPoint[]>(`${environment.apiBaseUrl}/api/analysis/fitness-trend`, { withCredentials: true })
      .pipe(
        tap(series => {
          this.series.set(series);
          this.loadState.set('loaded');
        })
      )
      .subscribe({ error: () => this.setFailed() });
  }

  private setFailed() {
    this.loadState.set('error');
  }
}
