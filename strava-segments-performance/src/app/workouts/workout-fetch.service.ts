import { Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { interval, switchMap, takeWhile, tap } from 'rxjs';
import { environment } from '../../environments/environment';

export type FetchStatusValue = 'idle' | 'pending' | 'running' | 'completed' | 'failed' | 'interrupted';
export type FetchStage = 'listing' | 'fetching_details' | null;

export interface WorkoutFetchStatus {
  status: FetchStatusValue;
  stage: FetchStage;
  activitiesProcessed: number;
  totalToProcess: number | null;
  errorMessage: string | null;
}

const IDLE_STATUS: WorkoutFetchStatus = {
  status: 'idle',
  stage: null,
  activitiesProcessed: 0,
  totalToProcess: null,
  errorMessage: null
};

@Injectable({ providedIn: 'root' })
export class WorkoutFetchService {
  status = signal<WorkoutFetchStatus>(IDLE_STATUS);

  constructor(private http: HttpClient) {}

  trigger() {
    return this.http
      .post<WorkoutFetchStatus>(`${environment.apiBaseUrl}/api/workouts/fetch`, {}, { withCredentials: true })
      .pipe(tap(status => this.status.set(status)))
      .subscribe(() => this.startPolling());
  }

  checkStatus() {
    return this.http
      .get<WorkoutFetchStatus>(`${environment.apiBaseUrl}/api/workouts/fetch-status`, { withCredentials: true })
      .pipe(
        tap(status => {
          this.status.set(status);
          if (status.status === 'pending' || status.status === 'running') {
            this.startPolling();
          }
        })
      )
      .subscribe();
  }

  private startPolling() {
    interval(2000)
      .pipe(
        switchMap(() =>
          this.http.get<WorkoutFetchStatus>(`${environment.apiBaseUrl}/api/workouts/fetch-status`, {
            withCredentials: true
          })
        ),
        tap(status => this.status.set(status)),
        takeWhile(status => status.status === 'pending' || status.status === 'running', true)
      )
      .subscribe();
  }
}
