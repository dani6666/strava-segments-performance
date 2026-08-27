import { Component, OnInit, computed } from '@angular/core';
import { AuthService } from '../auth/auth.service';
import { WorkoutFetchService } from '../workouts/workout-fetch.service';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.scss'
})
export class DashboardComponent implements OnInit {
  constructor(public authService: AuthService, public fetchService: WorkoutFetchService) {}

  invalidRange = computed(() => {
    const from = this.fetchService.fromDate();
    const to = this.fetchService.toDate();
    return !!from && !!to && from > to;
  });

  ngOnInit() {
    this.fetchService.checkStatus();
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
