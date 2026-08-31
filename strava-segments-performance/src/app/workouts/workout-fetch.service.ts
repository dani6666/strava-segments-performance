import { Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Subscription, interval, switchMap, takeWhile, tap } from 'rxjs';
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

export interface FetchWindowBody {
  after?: string;
  before?: string;
}

/** Local calendar day (YYYY-MM-DD) -> start of that day in the browser's timezone, as a UTC ISO instant. */
export function startOfLocalDayUtcIso(dateStr: string): string {
  const [year, month, day] = dateStr.split('-').map(Number);
  return new Date(year, month - 1, day, 0, 0, 0, 0).toISOString();
}

/** Local calendar day (YYYY-MM-DD) -> start of the NEXT day in the browser's timezone, as a UTC ISO instant. */
export function startOfNextLocalDayUtcIso(dateStr: string): string {
  const [year, month, day] = dateStr.split('-').map(Number);
  return new Date(year, month - 1, day + 1, 0, 0, 0, 0).toISOString();
}

@Injectable({ providedIn: 'root' })
export class WorkoutFetchService {
  status = signal<WorkoutFetchStatus>(IDLE_STATUS);
  fromDate = signal<string | null>(null);
  toDate = signal<string | null>(null);

  private pollingSub?: Subscription;

  constructor(private http: HttpClient) {}

  trigger() {
    return this.http
      .post<WorkoutFetchStatus>(`${environment.apiBaseUrl}/api/workouts/fetch`, this.buildFetchWindowBody(), {
        withCredentials: true
      })
      .pipe(tap(status => this.status.set(status)))
      .subscribe({
        next: () => this.startPolling(),
        error: err => this.setFailed(err)
      });
  }

  private buildFetchWindowBody(): FetchWindowBody {
    const body: FetchWindowBody = {};
    const from = this.fromDate();
    const to = this.toDate();
    if (from) body.after = startOfLocalDayUtcIso(from);
    if (to) body.before = startOfNextLocalDayUtcIso(to);
    return body;
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
      .subscribe({ error: err => this.setFailed(err) });
  }

  private startPolling() {
    // Guard against a second concurrent poller (e.g. trigger() after a
    // checkStatus()-initiated poll is already live).
    if (this.pollingSub && !this.pollingSub.closed) {
      return;
    }
    this.pollingSub = interval(2000)
      .pipe(
        switchMap(() =>
          this.http.get<WorkoutFetchStatus>(`${environment.apiBaseUrl}/api/workouts/fetch-status`, {
            withCredentials: true
          })
        ),
        tap(status => this.status.set(status)),
        takeWhile(status => status.status === 'pending' || status.status === 'running', true)
      )
      .subscribe({ error: err => this.setFailed(err) });
  }

  private setFailed(err: unknown) {
    this.status.update(current => ({
      ...current,
      status: 'failed',
      errorMessage: err instanceof Error ? err.message : 'Fetch request failed.'
    }));
  }
}
