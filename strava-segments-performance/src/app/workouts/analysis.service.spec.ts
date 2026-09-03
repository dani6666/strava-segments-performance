import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { environment } from '../../environments/environment';
import { AnalysisService } from './analysis.service';

describe('AnalysisService', () => {
  let service: AnalysisService;
  let httpMock: HttpTestingController;

  const trendUrl = `${environment.apiBaseUrl}/api/analysis/fitness-trend`;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(AnalysisService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('sends no query params when neither from nor to is provided', () => {
    service.load();

    const req = httpMock.expectOne((r) => r.url === trendUrl);
    expect(req.request.params.keys()).toEqual([]);
    req.flush([]);
  });

  it('forwards only the from param when to is omitted', () => {
    service.load('2026-08-01T00:00:00.000Z');

    const req = httpMock.expectOne((r) => r.url === trendUrl);
    expect(req.request.params.get('from')).toBe('2026-08-01T00:00:00.000Z');
    expect(req.request.params.has('to')).toBe(false);
    req.flush([]);
  });

  it('forwards only the to param when from is omitted', () => {
    service.load(undefined, '2026-08-16T00:00:00.000Z');

    const req = httpMock.expectOne((r) => r.url === trendUrl);
    expect(req.request.params.has('from')).toBe(false);
    expect(req.request.params.get('to')).toBe('2026-08-16T00:00:00.000Z');
    req.flush([]);
  });

  it('forwards both params when both dates are provided', () => {
    service.load('2026-08-01T00:00:00.000Z', '2026-08-16T00:00:00.000Z');

    const req = httpMock.expectOne((r) => r.url === trendUrl);
    expect(req.request.params.get('from')).toBe('2026-08-01T00:00:00.000Z');
    expect(req.request.params.get('to')).toBe('2026-08-16T00:00:00.000Z');
    req.flush([]);
  });
});
