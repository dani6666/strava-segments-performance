import { Component, OnInit, signal } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { AuthService } from '../auth/auth.service';

@Component({
  selector: 'app-login',
  standalone: true,
  templateUrl: './login.component.html',
  styleUrl: './login.component.scss'
})
export class LoginComponent implements OnInit {
  error = signal<string | null>(null);

  constructor(private route: ActivatedRoute, private authService: AuthService) {}

  ngOnInit() {
    const err = this.route.snapshot.queryParamMap.get('error');
    if (err) {
      this.error.set('Authentication failed. Please try again.');
    }
  }

  connect() {
    this.authService.login();
  }
}
