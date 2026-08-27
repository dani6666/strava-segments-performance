import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { DashboardComponent } from './dashboard.component';
import { WorkoutFetchService } from '../workouts/workout-fetch.service';

describe('DashboardComponent', () => {
  let component: DashboardComponent;
  let fetchService: WorkoutFetchService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])]
    });
    const fixture = TestBed.createComponent(DashboardComponent);
    component = fixture.componentInstance;
    fetchService = TestBed.inject(WorkoutFetchService);
  });

  it('invalidRange is false when both dates are blank', () => {
    expect(component.invalidRange()).toBe(false);
  });

  it('invalidRange is false when only one date is set', () => {
    fetchService.fromDate.set('2026-01-10');

    expect(component.invalidRange()).toBe(false);
  });

  it('invalidRange is false when from is before or equal to to', () => {
    fetchService.fromDate.set('2026-01-10');
    fetchService.toDate.set('2026-01-20');
    expect(component.invalidRange()).toBe(false);

    fetchService.toDate.set('2026-01-10');
    expect(component.invalidRange()).toBe(false);
  });

  it('invalidRange is true when from is after to', () => {
    fetchService.fromDate.set('2026-01-20');
    fetchService.toDate.set('2026-01-10');

    expect(component.invalidRange()).toBe(true);
  });
});
