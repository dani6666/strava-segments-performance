# syntax=docker/dockerfile:1

FROM node:22-alpine AS build
WORKDIR /app

COPY package.json package-lock.json ./
RUN npm ci

COPY . ./
RUN npm run build -- --configuration production

FROM nginx:1.27-alpine AS runtime

ENV BACKEND_ORIGIN=http://backend.railway.internal:8080
ENV FORWARDED_PROTO=https

RUN rm /etc/nginx/conf.d/default.conf

COPY nginx.conf /etc/nginx/conf.d/app.conf.template

COPY --from=build /app/dist/strava-segments-performance/browser /usr/share/nginx/html

EXPOSE 80
CMD ["/bin/sh", "-c", "envsubst '${BACKEND_ORIGIN} ${FORWARDED_PROTO}' < /etc/nginx/conf.d/app.conf.template > /etc/nginx/conf.d/app.conf && nginx -g 'daemon off;'"]
