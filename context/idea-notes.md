# Strava Segments Performance

### Main problem

When reviewing my activities and segment results after cycling workouts on Strava, it is difficult to determine my progress and whether my fitness is improving or declining. The Strava Segments Performance app compares segment results with the user’s historical performances in terms of time and heart rate for a given segment, and evaluates whether fitness has improved or worsened.

### Minimum feature set

- Login to the app using OAuth 
- Fetching and analysing workouts in the given timeframe
- Saves the fetched workouts in the database
- The app reuses fetched workouts in the next analysis
- Display a chart of the user’s fitness over time (the values on the chart will be a score from 0 to 100, where 100 represents peak fitness within a given time window, and 0 represents the lowest fitness within that same time window).

### What is not inlcuded in MVP

- Analysing the weather or surface of the segments to better measure the performance
- Adding new providers like Garmin Connect etc

### Success criteria

- The app performes the analysis of up to 1000 workouts and takes up to 30 seconds to display a summary (fetching of the workouts may take more time)
- The app displays fitness trend chart in the neat, user-friendly way