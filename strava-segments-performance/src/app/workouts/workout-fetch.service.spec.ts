import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { environment } from '../../environments/environment';
import { WorkoutFetchService, startOfLocalDayUtcIso, startOfNextLocalDayUtcIso } from './workout-fetch.service';

describe('WorkoutFetchService', () => {
  let service: WorkoutFetchService;
  let httpMock: HttpTestingController;

  const fetchUrl = `${environment.apiBaseUrl}/api/workouts/fetch`;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()]
    });
    service = TestBed.inject(WorkoutFetchService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('sends an empty body when no range is selected', () => {
    service.trigger();

    const req = httpMock.expectOne(fetchUrl);
    expect(req.request.body).toEqual({});
    req.flush({ status: 'pending', stage: null, activitiesProcessed: 0, totalToProcess: null, errorMessage: null });
  });

  it('sends after/before converted from local whole days to UTC when both dates are set', () => {
    service.fromDate.set('2026-01-15');
    service.toDate.set('2026-01-20');

    service.trigger();

    const req = httpMock.expectOne(fetchUrl);
    expect(req.request.body).toEqual({
      after: startOfLocalDayUtcIso('2026-01-15'),
      before: startOfNextLocalDayUtcIso('2026-01-20')
    });
    req.flush({ status: 'pending', stage: null, activitiesProcessed: 0, totalToProcess: null, errorMessage: null });
  });

  it('omits "before" when only the from date is set', () => {
    service.fromDate.set('2026-01-15');

    service.trigger();

    const req = httpMock.expectOne(fetchUrl);
    expect(req.request.body).toEqual({ after: startOfLocalDayUtcIso('2026-01-15') });
    req.flush({ status: 'pending', stage: null, activitiesProcessed: 0, totalToProcess: null, errorMessage: null });
  });

  it('omits "after" when only the to date is set', () => {
    service.toDate.set('2026-01-20');

    service.trigger();

    const req = httpMock.expectOne(fetchUrl);
    expect(req.request.body).toEqual({ before: startOfNextLocalDayUtcIso('2026-01-20') });
    req.flush({ status: 'pending', stage: null, activitiesProcessed: 0, totalToProcess: null, errorMessage: null });
  });
});
