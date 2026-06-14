import { Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Router } from '@angular/router';
import { tap } from 'rxjs';
import { environment } from '../../environments/environment';

export interface AuthUser {
  stravaAthleteId: number;
  displayName: string;
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  user = signal<AuthUser | null>(null);

  constructor(private http: HttpClient, private router: Router) {}

  checkAuth() {
    return this.http
      .get<AuthUser>(`${environment.apiBaseUrl}/api/auth/me`, { withCredentials: true })
      .pipe(tap(user => this.user.set(user)));
  }

  login() {
    window.location.href = `${environment.apiBaseUrl}/auth/login`;
  }

  logout() {
    this.http
      .post(`${environment.apiBaseUrl}/api/auth/logout`, {}, { withCredentials: true })
      .subscribe({
        next: () => {
          this.user.set(null);
          this.router.navigate(['/login']);
        },
        error: () => {
          this.user.set(null);
          this.router.navigate(['/login']);
        }
      });
  }
}
