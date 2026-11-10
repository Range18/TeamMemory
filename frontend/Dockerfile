FROM node:24-bookworm-slim AS build
WORKDIR /app
ENV NODE_OPTIONS="--max-old-space-size=384 --max-semi-space-size=4"
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY . ./
RUN npm run build

FROM nginx:stable-alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 80
HEALTHCHECK --interval=10s --timeout=3s --start-period=5s --retries=6 \
    CMD wget -q -O /dev/null http://127.0.0.1/ || exit 1
