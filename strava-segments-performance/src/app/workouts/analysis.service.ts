import { Injectable, signal } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
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

  load(from?: string, to?: string) {
    this.loadState.set('loading');
    let params = new HttpParams();
    if (from) params = params.set('from', from);
    if (to) params = params.set('to', to);
    return this.http
      .get<FitnessTrendPoint[]>(`${environment.apiBaseUrl}/api/analysis/fitness-trend`, {
        params,
        withCredentials: true,
      })
      .pipe(
        tap((series) => {
          this.series.set(series);
          this.loadState.set('loaded');
        }),
      )
      .subscribe({ error: () => this.setFailed() });
  }

  private setFailed() {
    this.loadState.set('error');
  }
}
