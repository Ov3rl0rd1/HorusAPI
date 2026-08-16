FROM nginx:alpine

# certbot for automatic Let's Encrypt certificates
# gettext provides envsubst (for rendering the nginx config template)
# curl + jq are used by sync-releases.sh to mirror the client release
RUN apk add --no-cache certbot gettext curl jq \
 && mkdir -p /var/www/certbot /var/www/downloads

COPY nginx.conf            /etc/nginx/nginx-template.conf
COPY nginx-http.conf       /etc/nginx/nginx-http-template.conf
COPY locations.conf        /etc/nginx/locations.conf
COPY security-headers.conf /etc/nginx/security-headers.conf
COPY html/                 /usr/share/nginx/html/
COPY sync-releases.sh  /sync-releases.sh
COPY entrypoint.sh     /entrypoint.sh
RUN chmod +x /entrypoint.sh /sync-releases.sh

ENTRYPOINT ["/entrypoint.sh"]
