import { ApplicationConfig, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient, withFetch } from '@angular/common/http';
import { provideCharts } from 'ng2-charts';
import { LineController, LineElement, PointElement, LinearScale, CategoryScale, Tooltip } from 'chart.js';

import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(withFetch()),
    provideCharts({ registerables: [LineController, LineElement, PointElement, LinearScale, CategoryScale, Tooltip] })
  ]
};
