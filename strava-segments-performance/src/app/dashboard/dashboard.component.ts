import { Component, OnInit } from '@angular/core';
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

  ngOnInit() {
    this.fetchService.checkStatus();
  }

  trigger() {
    this.fetchService.trigger();
  }
}
