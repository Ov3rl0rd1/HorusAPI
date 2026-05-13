FROM nginx:alpine

# certbot for automatic Let's Encrypt certificates
# gettext provides envsubst (for rendering the nginx config template)
RUN apk add --no-cache certbot gettext \
 && mkdir -p /var/www/certbot

COPY nginx.conf   /etc/nginx/nginx-template.conf
COPY html/        /usr/share/nginx/html/
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

ENTRYPOINT ["/entrypoint.sh"]
