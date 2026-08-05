# syntax=docker/dockerfile:1.7

FROM node:20-alpine AS build
WORKDIR /app

COPY src/Desicon.Workflow.Web/package*.json ./
RUN npm ci

COPY src/Desicon.Workflow.Web/ ./

# Vite inlines import.meta.env at compile time, so a static bundle has no
# runtime configuration -- these must be present during the build or the app
# ships pointing at nothing. None is a secret: a public client id and a tenant
# id are visible to anyone who opens the sign-in page, and the security
# boundary is the Entra redirect-URI allow-list plus the API's token
# validation, not the secrecy of an identifier.
ARG VITE_ENTRA_CLIENT_ID=""
ARG VITE_ENTRA_TENANT_ID=""
ARG VITE_API_SCOPE=""

# Empty means same-origin, which is the deployed shape: Front Door routes
# /api/* to the API and everything else here, so the browser makes no
# cross-origin call and the API needs no CORS entry.
ARG VITE_API_BASE_URL=""

ENV VITE_ENTRA_CLIENT_ID=$VITE_ENTRA_CLIENT_ID \
    VITE_ENTRA_TENANT_ID=$VITE_ENTRA_TENANT_ID \
    VITE_API_SCOPE=$VITE_API_SCOPE \
    VITE_API_BASE_URL=$VITE_API_BASE_URL

RUN npm run build

FROM nginx:1.27-alpine AS runtime

RUN rm /etc/nginx/conf.d/default.conf
COPY docker/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html

# Run as the unprivileged nginx user rather than root.
RUN chown -R nginx:nginx /usr/share/nginx/html /var/cache/nginx \
    && touch /var/run/nginx.pid \
    && chown nginx:nginx /var/run/nginx.pid
USER nginx

EXPOSE 8080
