import { Component, computed, input } from '@angular/core';
import { ChartConfiguration, ChartData } from 'chart.js';
import { BaseChartDirective } from 'ng2-charts';
import { FitnessTrendPoint } from '../workouts/analysis.service';

@Component({
  selector: 'app-fitness-trend-chart',
  standalone: true,
  imports: [BaseChartDirective],
  templateUrl: './fitness-trend-chart.component.html',
  styleUrl: './fitness-trend-chart.component.scss'
})
export class FitnessTrendChartComponent {
  series = input.required<FitnessTrendPoint[]>();

  chartData = computed<ChartData<'line'>>(() => ({
    labels: this.series().map(point => formatWorkoutDate(point.date)),
    datasets: [
      {
        data: this.series().map(point => point.score),
        label: 'Fitness score',
        borderColor: '#fc4c02',
        backgroundColor: '#fc4c02',
        pointRadius: 3,
        pointHoverRadius: 5,
        tension: 0.2
      }
    ]
  }));

  chartOptions: ChartConfiguration<'line'>['options'] = {
    responsive: true,
    maintainAspectRatio: false,
    scales: {
      y: { min: 0, max: 100 }
    },
    plugins: {
      tooltip: { intersect: false, mode: 'index' }
    }
  };
}

function formatWorkoutDate(isoDate: string): string {
  return new Date(isoDate).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
}
