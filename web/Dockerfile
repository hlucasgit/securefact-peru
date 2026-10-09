# IMAGE_REGISTRY is empty (Docker Hub) unless the build asks for a mirror, as the CI does when the shared runners exhaust the anonymous pull limit.
ARG IMAGE_REGISTRY=
FROM ${IMAGE_REGISTRY}library/node:24-alpine AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
RUN npm run build

# Static files behind nginx, which also proxies /api to the API: the browser sees one origin (no CORS).
ARG IMAGE_REGISTRY
FROM ${IMAGE_REGISTRY}nginxinc/nginx-unprivileged:stable-alpine AS final
# The base image lags behind the fixes of Alpine (the image scan fails on them): bring its packages up to date, then go back to the unprivileged user.
USER root
RUN apk upgrade --no-cache
USER 101
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 8080
