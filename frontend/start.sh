#!/bin/sh
# Generate config.js for the frontend SPA
printf 'window.ARMA3_API_BASE = "%s";\nwindow.ARMA3_REST_ONLY = %s;\n' \
    "${ARMA3_API_BASE:-}" "${ARMA3_REST_ONLY:-true}" \
    > /usr/share/nginx/html/config.js

# Substitute the API backend address into the nginx template.
# Default: api:8080  (bridge / compose DNS)
# Host networking: set ARMA3_API_BACKEND=10.89.0.1:8081 in .env
BACKEND="${ARMA3_API_BACKEND:-api:8080}"
sed "s|__ARMA3_API_BACKEND__|${BACKEND}|g" \
    /etc/nginx/conf.d/default.conf.template \
    > /etc/nginx/conf.d/default.conf

exec nginx -g 'daemon off;'
